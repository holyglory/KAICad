using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Every code a failed save can return, each explained in the save tool's description.
    private static readonly string[] SaveFailureCodes = ["file_not_writable", "native_save_refused", "native_save_failed",
        "partial_save", "file_changed_during_save", "file_unreadable_during_save", "save_outside_document"];

    // Real persistence failures and a cancelled save through the compiled MCP server. A refused or
    // cancelled save never reports success, replaces no file it does not name, keeps the unsaved
    // edit in the editor and leaves the other KiCad instance alone; after the cause is fixed, the
    // same work saves. Starts and ends with the document clean and saved.
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
            string saveDescription = await mcp.ToolDescription("kicad_document_save");
            foreach (string code in SaveFailureCodes)
                StringAssert.Contains(saveDescription, code + ",", "The save tool tells an agent what each failure code means.");
            var reportedCodes = new SortedSet<string>(StringComparer.Ordinal);

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
                // A pure write problem is not described as a refusal, and the advice is to make files writable.
                Assert.IsFalse(result.ErrorMessage.Contains("KiCad refused to save", StringComparison.Ordinal), $"{phase}: {result.ErrorMessage}");
                StringAssert.Contains(result.ErrorMessage, "make the file or folder writable", phase);
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
                reportedCodes.Add(result.ErrorCode);
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
            await OperationNotStarted(mcp, client, document, instanceId, cancelled, token);
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

            // 6. A full disk, a failing project settings write, and another program changing the
            // root file or making it unreadable during the save. The last save restores the
            // fixture's title block.
            var faults = Stopwatch.StartNew();
            await VerifyFaultedSavesKeepWork(mcp, client, document, processId, evidence, instanceId, original, reportedCodes, token);
            Console.WriteLine($"Faulted saves for {instanceId} took {faults.Elapsed.TotalSeconds:F1}s.");

            // 7. The project rules need KiCad processes of their own, so they run beside one of the
            // two fixture instances (the one whose socket sorts first) within the journey's time.
            if (string.CompareOrdinal(client.Endpoint, sibling.Endpoint) < 0)
            {
                var opening = Stopwatch.StartNew();
                await VerifyProjectOpeningRules(processId, await ObserveLifecycleState(client, document, token), sibling,
                    evidence, instanceId, reportedCodes, token);
                Console.WriteLine($"Project opening rules beside {instanceId} took {opening.Elapsed.TotalSeconds:F1}s.");
            }

            CollectionAssert.IsSubsetOf(reportedCodes.ToArray(), SaveFailureCodes, "Every failure code the editors returned is described.");
            Assert.AreEqual(siblingBefore, await SiblingSnapshot(mcp, sibling, token),
                "Failures and cancellations in one KiCad instance must leave the other instance untouched.");
            Console.WriteLine($"Save failure codes returned by real editors for {instanceId}: {string.Join(", ", reportedCodes)}.");
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

    // KiCad decides that a project is read-only when it opens it. A project another KiCad holds is
    // never opened for automation: KiCad refuses it at startup and exits (here the sibling fixture
    // KiCad really holds its project's lock). A project whose file was read-only when KiCad opened
    // it refuses every save, even once the file is writable again, until it is reopened. Both run in
    // KiCad processes started through the MCP server for this purpose; the fixture instances and
    // their files are only read.
    private static async Task VerifyProjectOpeningRules(int processId, DocumentLifecycleState saved, SiblingKiCad sibling,
        string evidence, string instanceId, ISet<string> reportedCodes, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux process environment and file modes.");
        string copy = Directory.CreateTempSubdirectory("kicad-readonly-project-").FullName;
        string state = Directory.CreateTempSubdirectory("kicad-readonly-project-mcp-").FullName;
        var runtimes = new List<string>();
        var modes = new Dictionary<string, UnixFileMode>();
        string? started = null;
        try
        {
            // The same KiCad build on the same display as the instance under test, with its own settings.
            string executable = new FileInfo($"/proc/{processId}/exe").ResolveLinkTarget(true)!.FullName;
            var inherited = (await File.ReadAllTextAsync($"/proc/{processId}/environ", token))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('=', 2))
                .Where(pair => pair.Length == 2).GroupBy(pair => pair[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
            var environment = new Dictionary<string, string>
                { ["XDG_CONFIG_HOME"] = Path.Combine(copy, "config"), ["XDG_CACHE_HOME"] = Path.Combine(copy, "cache") };
            foreach (string name in new[] { "DISPLAY", "KICAD_RUN_FROM_BUILD_DIR", "GTK_THEME" })
                if (inherited.TryGetValue(name, out var value)) environment[name] = value;
            Assert.IsTrue(environment.ContainsKey("DISPLAY"), "The instance under test runs on the fixture's display.");
            await using var mcp = await CancellableMcpClient.StartAsync(state,
                Path.Combine(evidence, instanceId + "-project-opening-mcp.stderr.log"), token, environment);

            // A copy of the saved document whose project file is read-only when KiCad opens it.
            string sourceProject = saved.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pro");
            string sourceDirectory = Path.GetDirectoryName(sourceProject)!;
            foreach (string file in saved.NativeFiles)
            {
                string relative = Path.GetRelativePath(sourceDirectory, file);
                Assert.IsFalse(relative.StartsWith("..", StringComparison.Ordinal), "Every document file lives in the project folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, relative))!);
                File.Copy(file, Path.Combine(copy, relative));
            }
            string project = Path.Combine(copy, Path.GetFileName(sourceProject));
            string root = Path.Combine(copy, Path.GetFileNameWithoutExtension(sourceProject) + ".kicad_sch");
            KeepReadOnly(project, modes);

            // Both KiCads start at once: one on the project the sibling fixture KiCad holds, one on the copy.
            string siblingProject = (await new NativeClient(new NngTransport(), sibling.Endpoint).HandshakeAsync(token)).ProjectPath;
            string siblingFolder = Path.GetDirectoryName(siblingProject)!;
            string[] SiblingFolder() => Directory.EnumerateFileSystemEntries(siblingFolder)
                .Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray();
            string[] siblingEntries = SiblingFolder();
            var clock = Stopwatch.StartNew();
            var (_, lockedCall) = mcp.StartTool("kicad_instance_start", new { executable, projectPath = siblingProject });
            var (_, readOnlyCall) = mcp.StartTool("kicad_instance_start", new { executable, projectPath = project });
            var lockedAnswered = lockedCall.ContinueWith(_ => clock.Elapsed, TaskScheduler.Default);
            var readOnlyAnswered = readOnlyCall.ContinueWith(_ => clock.Elapsed, TaskScheduler.Default);
            JsonElement Result(JsonElement message)
            {
                Assert.IsFalse(message.TryGetProperty("error", out var protocolError), protocolError.ToString());
                return message.GetProperty("result").Clone();
            }
            var locked = Result(await lockedCall.WaitAsync(TimeSpan.FromSeconds(60), token));
            var start = Result(await readOnlyCall.WaitAsync(TimeSpan.FromSeconds(60), token));
            if (!(start.TryGetProperty("isError", out var startFailed) && startFailed.GetBoolean()))
            {
                started = start.GetProperty("structuredContent").GetProperty("instanceId").GetString()!;
                runtimes.Add(NativeIpcEndpoint.RuntimeDirectory(started));
            }
            Console.WriteLine($"The start on the locked project answered after {(await lockedAnswered).TotalSeconds:F1}s, "
                + $"the start on the copy after {(await readOnlyAnswered).TotalSeconds:F1}s.");

            // 1. KiCad refuses to open the project the other KiCad holds, names its lock, and exits.
            Assert.IsTrue(locked.GetProperty("isError").GetBoolean(), "KiCad must not open a project another KiCad holds. " + locked.GetRawText());
            var refusal = locked.GetProperty("structuredContent");
            Assert.AreEqual("start_failed", refusal.GetProperty("code").GetString(), locked.GetRawText());
            var inspect = Regex.Match(refusal.GetProperty("message").GetString()!, @"inspect (/\S+)\.$");
            Assert.IsTrue(inspect.Success, locked.GetRawText());
            runtimes.Add(inspect.Groups[1].Value);
            string lockedLog = Path.Combine(inspect.Groups[1].Value, "native.log");
            File.Copy(lockedLog, Path.Combine(evidence, instanceId + "-locked-project-native.log"), true);
            string log = await File.ReadAllTextAsync(lockedLog, token);
            foreach (string text in new[] { siblingProject, "is already open by", "Automation project could not be loaded" })
                StringAssert.Contains(log, text, "KiCad names the other KiCad's lock as the reason.");
            // The refused KiCad left nothing in the other project's folder (it ran there, as KiCad does).
            CollectionAssert.AreEqual(siblingEntries, SiblingFolder(), "The refused KiCad must leave the other project's folder untouched.");
            Console.WriteLine($"KiCad refused to open {siblingProject}, which the other fixture KiCad holds: {refusal.GetProperty("message").GetString()}");

            // 2. The project file was read-only when KiCad opened the project, and is writable again now.
            RequireToolSuccess(start);
            string readOnlyInstance = started ?? throw new AssertFailedException("KiCad did not start on the copied project.");
            RestoreModes(modes);
            var opened = await mcp.Tool("kicad_schematic_open", new { instanceId = readOnlyInstance, path = root });
            RequireToolSuccess(opened);
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(opened.GetProperty("content").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            var editor = new NativeClient(new NngTransport(),
                NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(readOnlyInstance), "api.sock")));
            var title = await editor.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
            title.Title = "Unsaved read-only project work " + Guid.NewGuid().ToString("N");
            await editor.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = title }, token);
            var dirty = await ObserveLifecycleState(editor, document, token);
            Assert.IsTrue(dirty.NativeContentDirty, "The edit must be unsaved work in the editor.");
            var disk = dirty.NativeFiles.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
            var reply = await mcp.Tool("kicad_document_save", new { instanceId = readOnlyInstance,
                expectedStateJson = SchematicJson.Formatter.Format(dirty), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), "A refused save must not report success. " + reply.GetRawText());
            var result = LifecycleResult(reply);
            Assert.AreEqual(LifecycleOperationStatus.LosFailed, result.Status, result.ErrorMessage);
            Assert.AreEqual("native_save_refused", result.ErrorCode, result.ErrorMessage);
            Assert.IsEmpty(result.BlockedFiles, "Every file is writable; KiCad's own state refuses the save. " + result.ErrorMessage);
            Assert.IsEmpty(result.WrittenFiles, result.ErrorMessage);
            foreach (string text in new[] { "KiCad refused to save", Path.GetFileName(project),
                         "read-only because its project file was read-only when the project was opened",
                         "the file is writable now", "reopen the project in KiCad", "making files writable does not help",
                         "KiCad replaced none of the document's files.", "The editor still holds all unsaved changes." })
                StringAssert.Contains(result.ErrorMessage, text, result.ErrorMessage);
            Assert.IsFalse(result.ErrorMessage.Contains("project lock", StringComparison.Ordinal), "No lock is involved. " + result.ErrorMessage);
            Assert.AreEqual(dirty, result.ObservedState, "The receipt shows the editor after the refusal.");
            Assert.AreEqual(dirty, await ObserveLifecycleState(editor, document, token), "The editor state changed.");
            Assert.AreEqual(title.Title, (await editor.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = document }, token)).Title, "The unsaved edit was lost.");
            foreach (var (path, before) in disk)
            {
                CollectionAssert.AreEqual(before.Bytes, await File.ReadAllBytesAsync(path, token), path + " changed on disk.");
                Assert.AreEqual(before.Written, File.GetLastWriteTimeUtc(path), path + " was rewritten by a refused save.");
            }
            reportedCodes.Add(result.ErrorCode);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-save-read-only-project.json"),
                SchematicJson.Formatter.Format(result), token);
            Console.WriteLine($"Save refused (project opened read-only) in KiCad {started} at {clock.Elapsed.TotalSeconds:F1}s: {result.ErrorMessage}");
        }
        finally
        {
            RestoreModes(modes);
            if (started is not null) await StopStartedKiCad(started);
            foreach (string runtime in runtimes.Where(Directory.Exists)) Directory.Delete(runtime, true);
            Directory.Delete(copy, true);
            Directory.Delete(state, true);
        }
    }

    // The fixture KiCad process that serves this endpoint, found by the socket it was started with.
    private static int FixtureProcessId(NativeClient client)
    {
        string socket = client.Endpoint["ipc://".Length..];
        var owners = new List<int>();
        foreach (string entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out int pid)) continue;
            string[] arguments;
            try { arguments = File.ReadAllText(Path.Combine(entry, "cmdline")).Split('\0'); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            int flag = Array.IndexOf(arguments, "--api-socket");
            if (flag >= 0 && flag + 1 < arguments.Length && arguments[flag + 1] == socket) owners.Add(pid);
        }
        Assert.HasCount(1, owners, "Exactly one fixture KiCad serves " + client.Endpoint);
        return owners[0];
    }

    // Ends a KiCad this journey started for itself, found by its automation instance ID. The
    // fixture's own KiCad instances have other IDs and are never touched.
    private static async Task StopStartedKiCad(string instanceId)
    {
        foreach (string entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out int pid)) continue;
            string[] arguments;
            try { arguments = (await File.ReadAllTextAsync(Path.Combine(entry, "cmdline"))).Split('\0'); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            int flag = Array.IndexOf(arguments, "--automation");
            if (flag < 0 || flag + 1 >= arguments.Length || arguments[flag + 1] != instanceId) continue;
            Assert.AreEqual(0, SignalProcess(pid, 9), "The KiCad started for this check could not be stopped.");
            await WaitUntil(() =>
            {
                try { return ProcessState(pid) == 'Z'; }
                catch (IOException) { return true; }
            }, "the KiCad started for this check to exit", CancellationToken.None);
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

    private static async Task OperationNotStarted(CancellableMcpClient mcp, NativeClient client, DocumentSpecifier document,
        string instanceId, string operation, CancellationToken token)
    {
        var reply = await mcp.Tool("kicad_document_operation", new
            { instanceId, documentJson = SchematicJson.Formatter.Format(document), operationId = operation, processEpoch = client.Epoch });
        Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), reply.GetRawText());
        var error = reply.GetProperty("structuredContent");
        Assert.AreEqual("operation_not_started", error.GetProperty("code").GetString(), reply.GetRawText());
        string message = error.GetProperty("message").GetString()!;
        StringAssert.Contains(message, operation);
        StringAssert.Contains(message, "has no record of starting");
        StringAssert.Contains(message, "saved or closed nothing");
        // KiCad itself agrees: it has no receipt, so it saved or closed nothing for this operation, and
        // it says so with the fixed marker the MCP server matches rather than with prose.
        var native = await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadLifecycleOperation, LifecycleOperationResult>(
            new() { Document = document.Clone(), OperationId = operation, ProcessEpoch = client.Epoch }, token));
        Assert.AreEqual(3, native.Status, native.Message);
        StringAssert.StartsWith(native.Message, KiCad.Automation.Mcp.DocumentLifecycleTools.NativeUnknownOperation + ":", native.Message);
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

    private const int SigContinue = 18, SigStop = 19, SigTerminate = 15;

    // KiCad is held by a stop signal: its untraced threads show 'T' (its traced main thread 't').
    private static bool IsPaused(int processId) => Directory.EnumerateDirectories($"/proc/{processId}/task").Any(task =>
    {
        try
        {
            string stat = File.ReadAllText(Path.Combine(task, "stat"));
            return stat[stat.LastIndexOf(')') + 2] == 'T';
        }
        catch (IOException) { return false; }
    });

    /// <summary>
    /// strace attached to KiCad's main thread, which runs every API request, with fault rules that
    /// make one save meet a real system error, or stop, at an exact point. The trace (-y names each
    /// descriptor's file) shows where every injected fault landed. Detaching leaves KiCad running.
    /// </summary>
    private sealed class SyscallFault : IAsyncDisposable
    {
        private readonly Process strace;
        private readonly string log;
        private readonly StringBuilder messages = new();
        private readonly TaskCompletionSource attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task reader;
        private bool detached;

        private SyscallFault(Process strace, string log)
        {
            this.strace = strace; this.log = log;
            reader = Task.Run(async () =>
            {
                while (await strace.StandardError.ReadLineAsync() is { } line)
                {
                    lock (messages) messages.AppendLine(line);
                    if (line.EndsWith(" attached", StringComparison.Ordinal)) attached.TrySetResult();
                }
                attached.TrySetException(new IOException("strace exited before attaching."));
            });
        }

        internal static async Task<SyscallFault> AttachAsync(int kicad, string log, CancellationToken token, params string[] rules)
        {
            var start = new ProcessStartInfo("strace") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-p", kicad.ToString(System.Globalization.CultureInfo.InvariantCulture), "-y", "-o", log }.Concat(rules))
                start.ArgumentList.Add(argument);
            Process? process;
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new AssertFailedException("The fault-injection proofs need strace on this host and permission to trace "
                    + "the test's own KiCad processes (kernel.yama.ptrace_scope 0): " + error.Message);
            }
            var fault = new SyscallFault(process ?? throw new InvalidOperationException("strace could not be started."), log);
            try { await fault.attached.Task.WaitAsync(TimeSpan.FromSeconds(10), token); }
            catch (Exception error)
            {
                await fault.DisposeAsync();
                if (error is IOException or TimeoutException)
                    throw new AssertFailedException($"strace could not attach to KiCad {kicad}: {fault.Messages}");
                throw;
            }
            return fault;
        }

        internal string Messages { get { lock (messages) return messages.ToString(); } }

        // The traced system calls, once strace has detached and written them all.
        internal async Task<string[]> DetachAsync()
        {
            if (!detached)
            {
                detached = true;
                if (!strace.HasExited && SignalProcess(strace.Id, SigTerminate) != 0)
                    throw new InvalidOperationException("strace could not be stopped.");
                await strace.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await reader.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return File.Exists(log) ? await File.ReadAllLinesAsync(log) : [];
        }

        // Every fault this trace injected, one line per system call.
        internal static string[] Injected(string[] trace) =>
            trace.Where(line => line.EndsWith("(INJECTED)", StringComparison.Ordinal)).ToArray();

        public async ValueTask DisposeAsync()
        {
            try { if (!detached) await DetachAsync(); }
            catch (Exception) when (!strace.HasExited) { strace.Kill(); await strace.WaitForExitAsync(); }
            finally { strace.Dispose(); }
        }
    }

    // Real system errors in the middle of a save, injected by strace into KiCad's main thread: a
    // full disk before anything is written and part way through, the project settings failing after
    // every sheet, and another program changing the root file or making it unreadable while the
    // save runs. Each save keeps the exact unsaved edit, never reports success, names exactly the
    // files that reached the disk, and is retained for a repeat and a receipt query. Starts and
    // ends with the saved document; the final save restores the fixture's own title block.
    private static async Task VerifyFaultedSavesKeepWork(CancellableMcpClient mcp, NativeClient client,
        DocumentSpecifier document, int processId, string evidence, string instanceId, TitleBlockInfo original,
        ISet<string> reportedCodes, CancellationToken token)
    {
        string documentJson = SchematicJson.Formatter.Format(document);
        var edited = original.Clone();
        edited.Title = "Unsaved full-disk work " + Guid.NewGuid().ToString("N");
        // The title block revision is also stored in the project file, so a save writes every file.
        edited.Revision = "fault-" + Guid.NewGuid().ToString("N")[..8];
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = edited }, token);

        // The editor still holds exactly the unsaved edit.
        async Task<DocumentLifecycleState> Unsaved(string phase)
        {
            var state = await ObserveLifecycleState(client, document, token);
            Assert.IsTrue(state.NativeContentDirty, phase + ": the edit must still be unsaved work in the editor.");
            var title = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
            Assert.AreEqual(edited.Title, title.Title, phase + ": the unsaved edit was lost.");
            Assert.AreEqual(edited.Revision, title.Revision, phase + ": the unsaved edit was lost.");
            return state;
        }

        var initial = await Unsaved("before the faults");
        string root = initial.NativeFiles.Single(path => Path.GetFileName(path) == document.Project.Name + ".kicad_sch");
        string project = initial.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pro");
        string[] sheets = initial.NativeFiles.Where(path => Path.GetExtension(path) == ".kicad_sch").ToArray();
        Assert.IsGreaterThan(1, sheets.Length, "The fixture hierarchy needs a second sheet file for a save that stops part way.");

        // One save under one fault. External names files the test itself changed during the save.
        async Task<(LifecycleOperationResult Result, string[] Injected)> Faulted(string phase, string code, string[] rules,
            Func<Task>? whilePaused = null, Func<Task>? afterReply = null, string[]? external = null)
        {
            var before = await Unsaved(phase + " (before)");
            var disk = before.NativeFiles.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
            string operation = Guid.NewGuid().ToString("D");
            var request = new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(before), operationId = operation };
            JsonElement reply;
            string[] trace;
            await using (var fault = await SyscallFault.AttachAsync(processId, Path.Combine(evidence, $"{instanceId}-{phase}.strace"), token, rules))
            {
                try
                {
                    if (whilePaused is null) reply = await mcp.Tool("kicad_document_save", request);
                    else
                    {
                        var (_, pending) = mcp.StartTool("kicad_document_save", request);
                        await WaitUntil(() => IsPaused(processId), $"KiCad to stop inside the {phase} save", token);
                        Assert.IsFalse(pending.IsCompleted, $"{phase}: the save answered while KiCad was stopped.");
                        await whilePaused();
                        Assert.AreEqual(0, SignalProcess(processId, SigContinue), "KiCad could not be resumed.");
                        var message = await pending.WaitAsync(TimeSpan.FromSeconds(60), token);
                        Assert.IsFalse(message.TryGetProperty("error", out var protocolError), protocolError.ToString());
                        reply = message.GetProperty("result").Clone();
                    }
                }
                finally
                {
                    // Never leave KiCad stopped, whatever happened above.
                    if (whilePaused is not null) SignalProcess(processId, SigContinue);
                    trace = await fault.DetachAsync();
                }
            }
            await WaitUntil(() => !IsPaused(processId), "KiCad to run again after the trace", token);
            if (afterReply is not null) await afterReply();
            Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), $"{phase}: a failed save must not report success. {reply.GetRawText()}");
            var result = LifecycleResult(reply);
            Assert.AreEqual(LifecycleOperationStatus.LosFailed, result.Status, $"{phase}: {result.ErrorMessage}");
            Assert.AreEqual(code, result.ErrorCode, $"{phase}: {result.ErrorMessage}");
            StringAssert.Contains(result.ErrorMessage, "The editor still holds all unsaved changes.", phase);
            Assert.IsTrue(result.ObservedState.NativeContentDirty, phase + ": the receipt shows the editor with its unsaved changes.");
            var after = await Unsaved(phase);
            if (afterReply is null) Assert.AreEqual(after, result.ObservedState, phase + ": the receipt shows the editor after the failure.");
            // Only the files the result names as written changed; the others kept their bytes and time.
            CollectionAssert.IsSubsetOf(result.WrittenFiles.ToArray(), before.NativeFiles.ToArray(), phase);
            foreach (var (path, previous) in disk)
            {
                if (result.WrittenFiles.Contains(path) || (external?.Contains(path) ?? false)) continue;
                CollectionAssert.AreEqual(previous.Bytes, await File.ReadAllBytesAsync(path, token), $"{phase}: {path} changed on disk.");
                Assert.AreEqual(previous.Written, File.GetLastWriteTimeUtc(path), $"{phase}: {path} was rewritten.");
            }
            if (result.WrittenFiles.Contains(root))
                StringAssert.Contains(await File.ReadAllTextAsync(root, token), edited.Title, phase + ": the written root carries the edit.");
            // The failure is retained: repeating the request or reading the receipt returns it without writing.
            Assert.AreEqual(result, LifecycleResult(await mcp.Tool("kicad_document_save", request)), phase);
            var receipt = await mcp.Tool("kicad_document_operation", new { instanceId, documentJson, operationId = operation, processEpoch = client.Epoch });
            RequireToolSuccess(receipt);
            Assert.AreEqual(result, LifecycleResult(receipt), phase);
            reportedCodes.Add(result.ErrorCode);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-save-{phase}.json"), SchematicJson.Formatter.Format(result), token);
            Console.WriteLine($"Save failed ({phase}) for {instanceId}: {result.ErrorCode}; written [{string.Join(", ", result.WrittenFiles.Select(Path.GetFileName))}]; "
                + $"blocked [{string.Join(", ", result.BlockedFiles.Select(Path.GetFileName))}]: {result.ErrorMessage}");
            return (result, SyscallFault.Injected(trace));
        }

        // A full disk: every flush (fsync) fails with ENOSPC, the kernel's answer when no space is
        // left. The root file's flush fails first, so nothing is replaced.
        var (full, injected) = await Faulted("full-disk", "file_not_writable", ["-e", "trace=fsync", "-e", "inject=fsync:error=ENOSPC"]);
        Assert.HasCount(1, injected, "Only the first file's flush ran.");
        StringAssert.Contains(injected[0], Path.GetFileName(root) + ".kicad-save-", "The full disk stopped the root file.");
        CollectionAssert.AreEqual(new[] { root }, full.BlockedFiles.ToArray(), full.ErrorMessage);
        Assert.IsEmpty(full.WrittenFiles, full.ErrorMessage);
        foreach (string text in new[] { "No space left on device", "KiCad replaced none of the document's files.", "free disk space" })
            StringAssert.Contains(full.ErrorMessage, text, "full-disk");

        // Another program changes the root file after KiCad observed it and while the save runs
        // (KiCad stops at its first check of the root file). KiCad keeps that version.
        string[] pause = ["-P", root, "-e", "trace=access,faccessat,faccessat2", "-e", "inject=access,faccessat,faccessat2:signal=SIGSTOP:when=1"];
        byte[] rootBytes = await File.ReadAllBytesAsync(root, token);
        byte[] changedBytes = [.. rootBytes, .. Encoding.UTF8.GetBytes("\n")];
        var (changed, _) = await Faulted("changed-during-save", "file_changed_during_save", pause,
            whilePaused: () => File.WriteAllBytesAsync(root, changedBytes, token), external: [root]);
        Assert.IsEmpty(changed.WrittenFiles, changed.ErrorMessage);
        Assert.IsEmpty(changed.BlockedFiles, changed.ErrorMessage);
        foreach (string text in new[] { Path.GetFileName(root), "another program changed it after the save began", "decide which version to keep" })
            StringAssert.Contains(changed.ErrorMessage, text, "changed-during-save");
        CollectionAssert.AreEqual(changedBytes, await File.ReadAllBytesAsync(root, token), "KiCad kept the other program's version.");
        // The other program's change is undone, so the editor's loaded file matches the disk again.
        await File.WriteAllBytesAsync(root, rootBytes, token);
        Assert.AreEqual(initial, await Unsaved("changed-during-save restored"), "Restoring the root file restores the observation.");

        // Another program leaves the root file writable but unreadable while the save runs, so
        // KiCad cannot confirm that nobody changed it.
        var modes = new Dictionary<string, UnixFileMode>();
        try
        {
            var (unreadable, _) = await Faulted("unreadable-during-save", "file_unreadable_during_save", pause,
                whilePaused: () => { KeepWriteOnly(root, modes); return Task.CompletedTask; },
                afterReply: () => { RestoreModes(modes); return Task.CompletedTask; });
            Assert.IsEmpty(unreadable.WrittenFiles, unreadable.ErrorMessage);
            Assert.IsEmpty(unreadable.BlockedFiles, unreadable.ErrorMessage);
            Assert.AreEqual(NativeFileBaselineStatus.NfbsUnreadable,
                unreadable.ObservedState.FileBaselines.Single(file => file.Path == root).Status, "The receipt shows the unreadable file.");
            foreach (string text in new[] { Path.GetFileName(root), "could not read it to confirm", "Make the file readable again" })
                StringAssert.Contains(unreadable.ErrorMessage, text, "unreadable-during-save");
            Assert.AreEqual(initial, await Unsaved("unreadable-during-save restored"), "A readable root file restores the observation.");
        }
        finally { RestoreModes(modes); }

        // The disk fills up after the root file was replaced: the second file's flush (the third
        // fsync; each file has one for itself and one for its folder) fails.
        var (partial, partialFaults) = await Faulted("full-disk-part-way", "partial_save", ["-e", "trace=fsync", "-e", "inject=fsync:error=ENOSPC:when=3"]);
        CollectionAssert.AreEqual(new[] { root }, partial.WrittenFiles.ToArray(), partial.ErrorMessage);
        Assert.HasCount(1, partial.BlockedFiles, partial.ErrorMessage);
        string second = partial.BlockedFiles.Single();
        CollectionAssert.Contains(sheets, second, "The save stopped at the second sheet file.");
        Assert.AreNotEqual(root, second);
        Assert.HasCount(1, partialFaults);
        StringAssert.Contains(partialFaults[0], Path.GetFileName(second) + ".kicad-save-", "The fault hit the second file's flush.");
        foreach (string text in new[] { "Saving stopped part way.", "No space left on device", "Already written: " + Path.GetFileName(root),
                     Path.GetFileName(second), Path.GetFileName(project), "The files on disk now mix old and new content." })
            StringAssert.Contains(partial.ErrorMessage, text, "full-disk-part-way");

        // Writing the project settings fails after every sheet was replaced. The settings writer
        // keeps its system error to itself and the file is still writable, so no file is called
        // blocked and nothing claims the project file was written.
        var (settings, settingsFaults) = await Faulted("project-settings-write", "partial_save",
            ["-e", "trace=fsync", "-e", $"inject=fsync:error=ENOSPC:when={2 * sheets.Length + 1}"]);
        Assert.AreEqual(root, settings.WrittenFiles.FirstOrDefault(), "The root file is written first.");
        CollectionAssert.AreEquivalent(sheets, settings.WrittenFiles.ToArray(), settings.ErrorMessage);
        Assert.IsEmpty(settings.BlockedFiles, "A failure without a found reason never marks a file blocked. " + settings.ErrorMessage);
        Assert.HasCount(1, settingsFaults);
        StringAssert.Contains(settingsFaults[0], Path.GetFileName(project) + ".kicad-save-", "The fault hit the project file's flush.");
        foreach (string text in new[] { "KiCad could not write '" + Path.GetFileName(project) + "'", "gave no system reason", "Not written: " + Path.GetFileName(project) })
            StringAssert.Contains(settings.ErrorMessage, text, "project-settings-write");
        Assert.IsFalse(settings.ErrorMessage.Contains("KiCad cannot write", StringComparison.Ordinal), settings.ErrorMessage);

        // With the faults gone, restoring the fixture's title block saves every file.
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = original }, token);
        var restored = await mcp.Tool("kicad_document_save", new { instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(await ObserveLifecycleState(client, document, token)),
            operationId = Guid.NewGuid().ToString("D") });
        RequireToolSuccess(restored);
        var saved = LifecycleResult(restored);
        Assert.AreEqual(LifecycleOperationStatus.LosSaved, saved.Status);
        Assert.IsFalse(saved.ObservedState.NativeContentDirty);
        // Every sheet is written again; the project file already holds the restored revision.
        CollectionAssert.IsSubsetOf(sheets, saved.WrittenFiles.ToArray(), "A successful save writes every sheet again.");
    }

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

    // Another program's change: the file stays writable but nobody can read it.
    private static void KeepWriteOnly(string path, Dictionary<string, UnixFileMode> modes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux file modes.");
        modes.TryAdd(path, File.GetUnixFileMode(path));
        File.SetUnixFileMode(path, UnixFileMode.UserWrite);
    }

    private static void RestoreModes(Dictionary<string, UnixFileMode> modes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux file modes.");
        foreach (var (path, mode) in modes) File.SetUnixFileMode(path, mode);
        modes.Clear();
    }

    private sealed record SiblingKiCad(string Endpoint, string InstanceId, string Epoch);

    // The native session fixture runs two independent projects, each with its own socket in its
    // project folder, one or two levels below the fixture's own folder. Failures in one must not
    // reach the other.
    private static async Task<SiblingKiCad> SiblingInstance(NativeClient client, CancellationToken token)
    {
        string socket = client.Endpoint["ipc://".Length..];
        string[] others = [];
        string? fixture = Path.GetDirectoryName(socket);
        for (int level = 0; level < 2 && others.Length == 0; level++)
        {
            fixture = Path.GetDirectoryName(fixture)!;
            others = Directory.EnumerateFiles(fixture, "api.sock", new EnumerationOptions
                    { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Where(path => path != socket).ToArray();
        }
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

        internal static async Task<CancellableMcpClient> StartAsync(string state, string stderr, CancellationToken token,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var start = UpdateCommandTests.StartInfo();
            start.RedirectStandardInput = true;
            start.StandardInputEncoding = new UTF8Encoding(false); start.StandardOutputEncoding = new UTF8Encoding(false);
            // KiCad processes this server starts inherit its environment (display, configuration).
            foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
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

        // The description an agent reads for one tool, as the running server lists it.
        internal async Task<string> ToolDescription(string name)
        {
            string? cursor = null;
            do
            {
                var reply = await Start("tools/list", cursor is null ? new { } : (object)new { cursor }).Reply
                    .WaitAsync(TimeSpan.FromSeconds(60), token);
                var result = reply.GetProperty("result");
                foreach (var tool in result.GetProperty("tools").EnumerateArray())
                    if (tool.GetProperty("name").GetString() == name) return tool.GetProperty("description").GetString()!;
                cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            }
            while (cursor is not null);
            throw new AssertFailedException($"The MCP server does not list {name}.");
        }

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
