using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task SaveCheckedThroughMcp(NativeClient client, DocumentSpecifier document,
        string evidence, CancellationToken token, bool verifyReconnect = true)
    {
        string statePath = Directory.CreateTempSubdirectory("kicad-save-mcp-").FullName;
        try
        {
            var before = await ObserveLifecycleState(client, document, token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            Assert.AreEqual(client.Epoch, before.ProcessEpoch);
            string operation = Guid.NewGuid().ToString("D");
            string expectedJson = SchematicJson.Formatter.Format(before);
            LifecycleOperationResult Parse(JsonElement reply) => SchematicJson.Parser.Parse<LifecycleOperationResult>(
                reply.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
                    .GetProperty("text").GetString()!);
            LifecycleOperationResult saved;
            await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "checked-save-" + operation + ".stderr.log"), token))
            {
                var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                var stale = before.Clone(); stale.Revision.Sequence++;
                var rejected = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = SchematicJson.Formatter.Format(stale), operationId = Guid.NewGuid().ToString("D") });
                Assert.IsTrue(rejected.GetProperty("isError").GetBoolean());
                Assert.AreEqual(LifecycleOperationStatus.LosRejected, Parse(rejected).Status);
                Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));

                if (before.NativeContentDirty || before.CleanCheckpointSha256 != before.StateSha256)
                {
                    var dirtyClose = await mcp.Tool("kicad_document_close", new
                        { instanceId, expectedStateJson = expectedJson, operationId = Guid.NewGuid().ToString("D") });
                    Assert.IsTrue(dirtyClose.GetProperty("isError").GetBoolean());
                    Assert.AreEqual(LifecycleOperationStatus.LosRejected, Parse(dirtyClose).Status);
                    Assert.AreEqual(before, await ObserveLifecycleState(client, document, token),
                        "Refused close must preserve both native state and dirty edits.");
                }

                var reply = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = expectedJson, operationId = operation });
                Assert.IsFalse(reply.TryGetProperty("isError", out error) && error.GetBoolean(), reply.GetRawText());
                saved = Parse(reply);
                Assert.AreEqual(LifecycleOperationStatus.LosSaved, saved.Status);
                Assert.AreEqual(document, saved.Document);
                Assert.IsFalse(saved.ObservedState.NativeContentDirty);
                var persisted = saved.ObservedState.NativeFiles.ToDictionary(path => path,
                    path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
                var repeated = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = expectedJson, operationId = operation });
                Assert.AreEqual(saved, Parse(repeated));
                foreach (var (path, expected) in persisted)
                {
                    CollectionAssert.AreEqual(expected.Bytes, File.ReadAllBytes(path));
                    Assert.AreEqual(expected.Written, File.GetLastWriteTimeUtc(path), "Replaying a save receipt must not write again.");
                }
                var mismatched = before.Clone(); mismatched.Revision.Sequence++;
                var conflict = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = SchematicJson.Formatter.Format(mismatched), operationId = operation });
                Assert.IsTrue(conflict.GetProperty("isError").GetBoolean());
                Assert.AreEqual(saved.ObservedState, await ObserveLifecycleState(client, document, token));
            }
            // Repeated middle steps may omit only this process-reconnection proof.
            // All per-edit persistence, stale/dirty refusal and replay assertions above remain.
            if (!verifyReconnect) return;
            // A new MCP process must query the existing native receipt without saving/reopening.
            await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "checked-save-reconnect-" + operation + ".stderr.log"), token))
            {
                var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                var receipt = await mcp.Tool("kicad_document_operation", new
                    { instanceId, documentJson = SchematicJson.Formatter.Format(document), operationId = operation, processEpoch = client.Epoch });
                Assert.IsFalse(receipt.TryGetProperty("isError", out error) && error.GetBoolean());
                Assert.AreEqual(saved, Parse(receipt));
                Assert.AreEqual(saved.ObservedState, await ObserveLifecycleState(client, document, token));
            }
        }
        finally { Directory.Delete(statePath, true); }
    }

    // Real persistence failures and a cancelled save through the compiled MCP server. A refused or
    // cancelled save never reports success, replaces no file, keeps the unsaved edit in the editor
    // and leaves the other KiCad instance alone; after the cause is fixed, the same work saves.
    // Starts and ends with the document clean and saved.
    private static async Task VerifySaveFailuresKeepWork(NativeClient client, DocumentSpecifier document,
        int processId, string evidence, string instanceId, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux file modes and process signals.");
        var elapsed = Stopwatch.StartNew();
        var sibling = await SiblingInstance(client, token);
        string statePath = Directory.CreateTempSubdirectory("kicad-save-failure-mcp-").FullName;
        string storage = Directory.CreateTempSubdirectory("kicad-readonly-storage-").FullName;
        var modes = new Dictionary<string, UnixFileMode>();
        string? linkedSheet = null;
        try
        {
            await using var mcp = await CancellableMcpClient.StartAsync(statePath,
                Path.Combine(evidence, instanceId + "-save-failures-mcp.stderr.log"), token);
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = sibling.Endpoint, expectedInstanceId = sibling.InstanceId }));
            string siblingBefore = await SiblingSnapshot(mcp, sibling, token);
            string documentJson = SchematicJson.Formatter.Format(document);

            var original = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
            var clean = await ObserveLifecycleState(client, document, token);
            Assert.IsFalse(clean.NativeContentDirty, "Failure cases start from a saved document.");
            string unsaved = "Unsaved lifecycle work " + Guid.NewGuid().ToString("N");
            var edited = original.Clone(); edited.Title = unsaved;
            await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = edited }, token);
            var dirty = await ObserveLifecycleState(client, document, token);
            Assert.IsTrue(dirty.NativeContentDirty, "The edit must be unsaved work in the editor.");
            string expectedStateJson = SchematicJson.Formatter.Format(dirty);
            string root = dirty.NativeFiles.Single(path => Path.GetFileName(path) == document.Project.Name + ".kicad_sch");
            string project = dirty.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pro");
            string sheet = dirty.NativeFiles.First(path => path != root && Path.GetExtension(path) == ".kicad_sch");

            Dictionary<string, (byte[] Bytes, DateTime Written)> Disk() => dirty.NativeFiles.ToDictionary(path => path,
                path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));
            // The editor still holds exactly the unsaved edit, and no document file changed.
            async Task WorkKept(string phase, Dictionary<string, (byte[] Bytes, DateTime Written)> disk)
            {
                Assert.AreEqual(dirty, await ObserveLifecycleState(client, document, token), phase + ": the editor state changed.");
                Assert.AreEqual(unsaved, (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                    new() { Document = document }, token)).Title, phase + ": the unsaved edit was lost.");
                foreach (var (path, before) in disk)
                {
                    CollectionAssert.AreEqual(before.Bytes, await File.ReadAllBytesAsync(path, token), $"{phase}: {path} changed on disk.");
                    Assert.AreEqual(before.Written, File.GetLastWriteTimeUtc(path), $"{phase}: {path} was rewritten.");
                }
            }
            async Task SaveRefused(string phase, string[] blocked, string[] mentions)
            {
                var disk = Disk();
                string operation = Guid.NewGuid().ToString("D");
                var request = new { instanceId, expectedStateJson, operationId = operation };
                var reply = await mcp.Tool("kicad_document_save", request);
                Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), $"{phase}: a failed save must not report success. {reply.GetRawText()}");
                var result = LifecycleResult(reply);
                Assert.AreEqual(LifecycleOperationStatus.LosFailed, result.Status, phase);
                Assert.AreEqual("file_not_writable", result.ErrorCode, $"{phase}: {result.ErrorMessage}");
                CollectionAssert.AreEquivalent(blocked, result.BlockedFiles.ToArray(), $"{phase}: {result.ErrorMessage}");
                Assert.IsEmpty(result.WrittenFiles, $"{phase}: KiCad must refuse before replacing any file.");
                foreach (string text in blocked.Select(path => Path.GetFileName(path)).Concat(mentions))
                    StringAssert.Contains(result.ErrorMessage, text, $"{phase}: the error must name the cause.");
                StringAssert.Contains(result.ErrorMessage, "KiCad replaced none of the document's files.", phase);
                StringAssert.Contains(result.ErrorMessage, "The editor still holds all unsaved changes.", phase);
                Assert.AreEqual(dirty, result.ObservedState, $"{phase}: the receipt shows the editor after the failure.");
                await WorkKept(phase, disk);
                // The failure is retained: repeating the request or reading the receipt returns it without writing.
                Assert.AreEqual(result, LifecycleResult(await mcp.Tool("kicad_document_save", request)), phase);
                var receipt = await mcp.Tool("kicad_document_operation", new
                    { instanceId, documentJson, operationId = operation, processEpoch = client.Epoch });
                RequireToolSuccess(receipt);
                Assert.AreEqual(result, LifecycleResult(receipt), phase);
                await WorkKept(phase + " replay", disk);
                await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-save-{phase}.json"),
                    SchematicJson.Formatter.Format(result), token);
                Console.WriteLine($"Save refused ({phase}) for {instanceId}: {result.ErrorMessage}");
            }

            // 1. Read-only schematic and project files: every blocked file is named at once.
            foreach (string path in new[] { root, project }) KeepReadOnly(path, modes);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => new FileStream(root, FileMode.Open, FileAccess.Write).Dispose(),
                "This account must really be unable to write the read-only fixture file.");
            await SaveRefused("read-only-files", [root, project], ["the file is read-only"]);
            RestoreModes(modes);

            // 2. A sheet stored in a folder that does not accept new files. KiCad replaces a file
            // through a temporary file beside it, so it refuses the whole save before touching the root.
            string stored = Path.Combine(storage, Path.GetFileName(sheet));
            File.Copy(sheet, stored);
            File.Delete(sheet);
            File.CreateSymbolicLink(sheet, stored);
            linkedSheet = sheet;
            Assert.AreEqual(dirty, await ObserveLifecycleState(client, document, token), "The same bytes behind a link are not a document change.");
            KeepReadOnly(storage, modes);
            await SaveRefused("read-only-folder", [sheet], [Path.GetFileName(storage), "does not allow KiCad to create or replace files"]);
            RestoreModes(modes);

            // 3. The agent cancels a save while KiCad is not answering. The request never reaches
            // KiCad, the cancelled call reports nothing, and the operation stays safe to repeat.
            var beforeCancel = Disk();
            string cancelled = Guid.NewGuid().ToString("D");
            var cancelledRequest = new { instanceId, expectedStateJson, operationId = cancelled };
            var cancelledReply = await CancelWhileKiCadIsStopped(mcp, processId, "kicad_document_save", cancelledRequest, token);
            await OperationNotReceived(mcp, client, document, instanceId, cancelled, token);
            await WorkKept("cancelled save", beforeCancel);
            NotReportedAsSuccess(cancelledReply, "cancelled save");

            // 4. With the folder writable again, repeating the exact cancelled request saves once,
            // through the link rather than over it, and a replay writes nothing.
            var saveReply = await mcp.Tool("kicad_document_save", cancelledRequest);
            RequireToolSuccess(saveReply);
            var saved = LifecycleResult(saveReply);
            Assert.AreEqual(LifecycleOperationStatus.LosSaved, saved.Status);
            Assert.IsFalse(saved.ObservedState.NativeContentDirty);
            Assert.IsEmpty(saved.BlockedFiles);
            CollectionAssert.IsSubsetOf(new[] { root, sheet }, saved.WrittenFiles.ToArray(), "The save reports the files it replaced.");
            CollectionAssert.IsSubsetOf(saved.WrittenFiles.ToArray(), dirty.NativeFiles.ToArray());
            StringAssert.Contains(await File.ReadAllTextAsync(root, token), unsaved, "The kept edit reached the root file.");
            Assert.IsNotNull(new FileInfo(sheet).LinkTarget, "Saving writes through a linked sheet, never over the link.");
            var written = saved.WrittenFiles.ToDictionary(path => path, path => File.GetLastWriteTimeUtc(path));
            Assert.AreEqual(saved, LifecycleResult(await mcp.Tool("kicad_document_save", cancelledRequest)));
            foreach (var (path, time) in written)
                Assert.AreEqual(time, File.GetLastWriteTimeUtc(path), "Replaying a saved operation must not write again.");
            NotReportedAsSuccess(cancelledReply, "cancelled save after its repetition");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-save-after-cancel.json"),
                SchematicJson.Formatter.Format(saved), token);

            byte[] sheetBytes = await File.ReadAllBytesAsync(sheet, token);
            File.Delete(sheet);
            await File.WriteAllBytesAsync(sheet, sheetBytes, token);
            linkedSheet = null;
            Assert.AreEqual(saved.ObservedState, await ObserveLifecycleState(client, document, token),
                "Replacing the link with the same bytes keeps the saved checkpoint.");

            // 5. A cancelled close leaves the editor open with the same saved document.
            await VerifyCancelledCloseKeepsEditor(mcp, client, document, processId, evidence, instanceId, token);

            // Restore the fixture title through one more checked save.
            await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = original }, token);
            var restored = await mcp.Tool("kicad_document_save", new { instanceId,
                expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, document, token)),
                operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(restored);
            Assert.AreEqual(LifecycleOperationStatus.LosSaved, LifecycleResult(restored).Status);

            Assert.AreEqual(siblingBefore, await SiblingSnapshot(mcp, sibling, token),
                "Failures and cancellations in one KiCad instance must leave the other instance untouched.");
            Console.WriteLine($"Save failure and cancellation journey for {instanceId} took {elapsed.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            RestoreModes(modes);
            if (linkedSheet is not null && new FileInfo(linkedSheet).LinkTarget is { } target)
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(storage, Path.GetFileName(target)), CancellationToken.None);
                File.Delete(linkedSheet);
                await File.WriteAllBytesAsync(linkedSheet, bytes, CancellationToken.None);
            }
            Directory.Delete(storage, true);
            Directory.Delete(statePath, true);
        }
    }

    private static LifecycleOperationResult LifecycleResult(JsonElement reply) => SchematicJson.Parser.Parse<LifecycleOperationResult>(
        reply.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString()!);

    // A cancelled call gets no reply under the MCP protocol; if one ever arrives it must not claim success.
    private static void NotReportedAsSuccess(Task<JsonElement> reply, string phase)
    {
        if (!reply.IsCompletedSuccessfully) return;
        var message = reply.Result;
        Assert.IsFalse(message.TryGetProperty("result", out var result)
            && !(result.TryGetProperty("isError", out var error) && error.GetBoolean()),
            $"{phase}: a cancelled call reported success: {message.GetRawText()}");
    }

    private static async Task OperationNotReceived(CancellableMcpClient mcp, NativeClient client, DocumentSpecifier document,
        string instanceId, string operation, CancellationToken token)
    {
        var reply = await mcp.Tool("kicad_document_operation", new
            { instanceId, documentJson = SchematicJson.Formatter.Format(document), operationId = operation, processEpoch = client.Epoch });
        Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), reply.GetRawText());
        var error = reply.GetProperty("structuredContent");
        Assert.AreEqual("operation_not_received", error.GetProperty("code").GetString(), reply.GetRawText());
        string message = error.GetProperty("message").GetString()!;
        StringAssert.Contains(message, operation);
        StringAssert.Contains(message, "never received");
        // KiCad itself agrees: it has no receipt, so it saved or closed nothing for this operation.
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadLifecycleOperation, LifecycleOperationResult>(
            new() { Document = document.Clone(), OperationId = operation, ProcessEpoch = client.Epoch }, token));
    }

    // Holds KiCad still (SIGSTOP) so the MCP server's request cannot reach it, cancels the call the
    // way an agent that stops waiting does, and resumes KiCad only after that connection closed.
    private static async Task<Task<JsonElement>> CancelWhileKiCadIsStopped(CancellableMcpClient mcp, int kicad,
        string tool, object arguments, CancellationToken token)
    {
        int idle = await mcp.SettledSocketCount(token);
        Assert.AreEqual(0, SignalProcess(kicad, SigStop), "KiCad could not be paused.");
        try
        {
            await WaitUntil(() => ProcessState(kicad) == 'T', "KiCad to pause", token);
            var (id, reply) = mcp.StartTool(tool, arguments);
            await WaitUntil(() => mcp.SocketCount() > idle, $"the MCP server to connect to the paused KiCad for {tool}", token);
            Assert.IsFalse(reply.IsCompleted, "A paused KiCad cannot answer.");
            await mcp.Cancel(id, "The agent stopped waiting for KiCad.");
            await WaitUntil(() => mcp.SocketCount() == idle, $"the cancelled {tool} connection to close", token);
            NotReportedAsSuccess(reply, "cancelled " + tool);
            return reply;
        }
        finally
        {
            if (SignalProcess(kicad, SigContinue) != 0) throw new InvalidOperationException("KiCad could not be resumed.");
            await WaitUntil(() => ProcessState(kicad) != 'T', "KiCad to resume", CancellationToken.None);
        }
    }

    private const int SigContinue = 18, SigStop = 19;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SignalProcess(int processId, int signal);

    private static char ProcessState(int processId)
    {
        string stat = File.ReadAllText($"/proc/{processId}/stat");
        return stat[stat.LastIndexOf(')') + 2];
    }

    private static async Task WaitUntil(Func<bool> condition, string what, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            try { await Task.Delay(20, limit.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new AssertFailedException($"Timed out waiting for {what}."); }
        }
    }

    private static void KeepReadOnly(string path, Dictionary<string, UnixFileMode> modes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux file modes.");
        var mode = File.GetUnixFileMode(path);
        modes.TryAdd(path, mode);
        File.SetUnixFileMode(path, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }

    private static void RestoreModes(Dictionary<string, UnixFileMode> modes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux file modes.");
        foreach (var (path, mode) in modes) File.SetUnixFileMode(path, mode);
        modes.Clear();
    }

    private sealed record SiblingKiCad(string Endpoint, string InstanceId, string Epoch);

    // The native session fixture runs two independent projects, each with its own socket beside
    // the other's project folder. Failures in one must not reach the other.
    private static async Task<SiblingKiCad> SiblingInstance(NativeClient client, CancellationToken token)
    {
        string socket = client.Endpoint["ipc://".Length..];
        string fixture = Path.GetDirectoryName(Path.GetDirectoryName(socket))!;
        var others = Directory.EnumerateDirectories(fixture).Select(directory => Path.Combine(directory, "api.sock"))
            .Where(path => path != socket && File.Exists(path)).ToArray();
        Assert.HasCount(1, others, "The native session fixture runs exactly one other KiCad project.");
        var peer = new NativeClient(new NngTransport(), "ipc://" + others[0]);
        var session = await peer.HandshakeAsync(token);
        Assert.AreNotEqual(client.Epoch, session.Epoch, "The sibling must be a different KiCad process.");
        return new(peer.Endpoint, session.InstanceId, session.Epoch);
    }

    // Everything an agent can observe about the other instance: its process epoch, its open
    // schematics with their full native state, and the bytes of their files.
    private static async Task<string> SiblingSnapshot(CancellableMcpClient mcp, SiblingKiCad sibling, CancellationToken token)
    {
        var text = new StringBuilder();
        var session = await new NativeClient(new NngTransport(), sibling.Endpoint).HandshakeAsync(token);
        Assert.AreEqual(sibling.Epoch, session.Epoch, "The other KiCad instance restarted.");
        text.AppendLine(session.InstanceId).AppendLine(session.Epoch);
        var listed = await mcp.Tool("kicad_documents_list", new { instanceId = sibling.InstanceId, kind = "schematic" });
        text.AppendLine(listed.GetRawText());
        if (listed.TryGetProperty("isError", out var failed) && failed.GetBoolean()) return text.ToString();
        var documents = SchematicJson.Parser.Parse<GetOpenDocumentsResponse>(listed.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
        foreach (var document in documents.Documents)
        {
            var state = await mcp.Tool("kicad_document_state", new
                { instanceId = sibling.InstanceId, documentJson = SchematicJson.Formatter.Format(document) });
            RequireToolSuccess(state);
            text.AppendLine(state.GetRawText());
            foreach (string file in LifecycleResultState(state).NativeFiles)
                text.Append(file).Append(' ').AppendLine(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, token))));
        }
        return text.ToString();
    }

    private static DocumentLifecycleState LifecycleResultState(JsonElement reply) => SchematicJson.Parser.Parse<DocumentLifecycleState>(
        reply.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString()!);

    /// <summary>
    /// A compiled STDIO MCP client that can cancel a call in flight (notifications/cancelled), as an
    /// agent does when it stops waiting. It owns only its MCP child, never KiCad.
    /// </summary>
    private sealed class CancellableMcpClient : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationToken token;
        private readonly SemaphoreSlim writes = new(1, 1);
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
        private readonly Task reader, diagnostics;
        private int nextId;

        private CancellableMcpClient(Process process, string stderr, CancellationToken token)
        {
            this.process = process; this.token = token;
            diagnostics = Task.Run(async () =>
            {
                await using var output = File.Create(stderr);
                await process.StandardError.BaseStream.CopyToAsync(output);
            });
            reader = Task.Run(Read);
        }

        internal static async Task<CancellableMcpClient> StartAsync(string state, string stderr, CancellationToken token)
        {
            var start = UpdateCommandTests.StartInfo();
            start.RedirectStandardInput = true;
            start.StandardInputEncoding = new UTF8Encoding(false); start.StandardOutputEncoding = new UTF8Encoding(false);
            start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
            var client = new CancellableMcpClient(Process.Start(start)!, stderr, token);
            try
            {
                var (_, initialized) = client.Start("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                    clientInfo = new { name = "kicad-cancellation-journey", version = "1" } });
                var reply = await initialized.WaitAsync(TimeSpan.FromSeconds(30), token);
                Assert.IsTrue(reply.TryGetProperty("result", out _), reply.GetRawText());
                await client.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
                return client;
            }
            catch { await client.DisposeAsync(); throw; }
        }

        internal async Task<JsonElement> Tool(string name, object arguments)
        {
            var reply = await StartTool(name, arguments).Reply.WaitAsync(TimeSpan.FromSeconds(60), token);
            if (reply.TryGetProperty("error", out var error)) throw new IOException(error.GetRawText());
            return reply.GetProperty("result").Clone();
        }

        internal (int Id, Task<JsonElement> Reply) StartTool(string name, object arguments) =>
            Start("tools/call", new { name, arguments });

        internal Task Cancel(int id, string reason) =>
            Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id, reason } });

        // NNG opens one socket per native exchange and closes it when the exchange ends.
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

        private (int Id, Task<JsonElement> Reply) Start(string method, object parameters)
        {
            int id = Interlocked.Increment(ref nextId);
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) pending.Add(id, reply);
            _ = Send(new { jsonrpc = "2.0", id, method, @params = parameters }).ContinueWith(sent =>
            {
                if (sent.Exception is not null) reply.TrySetException(sent.Exception.InnerExceptions);
            }, TaskScheduler.Default);
            return (id, reply.Task);
        }

        private async Task Send(object message)
        {
            await writes.WaitAsync(token);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            finally { writes.Release(); }
        }

        private async Task Read()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var parsed = JsonDocument.Parse(line);
                    var message = parsed.RootElement;
                    if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                        || message.TryGetProperty("method", out _)) continue;
                    TaskCompletionSource<JsonElement>? reply;
                    lock (pending) pending.Remove(id.GetInt32(), out reply);
                    reply?.TrySetResult(message.Clone());
                }
            }
            finally
            {
                lock (pending)
                {
                    foreach (var reply in pending.Values) reply.TrySetException(new IOException("The MCP server exited before replying."));
                    pending.Clear();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(reader, diagnostics).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { process.Dispose(); writes.Dispose(); }
        }
    }
}
