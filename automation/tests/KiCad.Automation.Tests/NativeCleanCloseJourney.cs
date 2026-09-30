using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Native contracts precede this real MCP/editor journey in the governed graph.
    private static async Task VerifyCleanPcbClose(NativeClient client, DocumentSpecifier board,
        DocumentSpecifier schematic, string evidence, CancellationToken token)
    {
        var pcbClose = await VerifyCleanDocumentClose(client, board, schematic, openTool: "kicad_pcb_open", evidence, token);
        var schematicClose = await VerifyCleanDocumentClose(client, schematic, board, openTool: "kicad_schematic_open", evidence, token);
        CollectionAssert.AreEqual(pcbClose.BothOpen, schematicClose.BothOpen, "Reopening an editor must restore the same handled requests.");
        CollectionAssert.AreEqual(pcbClose.Features, schematicClose.Features, "Feature contracts belong to the build, not to the open editors.");
        // Every request handled with both editors open is still handled after closing one editor or
        // the other: the manager's, each editor's own and those both editors share. So losing any
        // type that only the editor left open handles would show here.
        string[] union = pcbClose.AfterClose.Union(schematicClose.AfterClose).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(pcbClose.BothOpen, union,
            "With both editors open the handshake must list exactly the requests of the PCB-only and schematic-only states together.");
        Console.WriteLine($"Handled requests of {client.Endpoint}: {pcbClose.BothOpen.Length} with both editors open, "
            + $"{pcbClose.AfterClose.Length} with only the schematic editor, {schematicClose.AfterClose.Length} with only the PCB editor; "
            + $"features [{string.Join(", ", pcbClose.Features)}] in every state.");
    }

    // Returns the first pass's probed handled requests with both editors open and after closing this
    // one, and the feature contracts, which stay the same with both editors open, after the close and
    // after the reopen in both passes.
    private static async Task<(string[] BothOpen, string[] AfterClose, string[] Features)> VerifyCleanDocumentClose(NativeClient client,
        DocumentSpecifier document, DocumentSpecifier otherDocument, string openTool, string evidence, CancellationToken token)
    {
        (string[] BothOpen, string[] AfterClose) firstPass = (Array.Empty<string>(), Array.Empty<string>());
        string[]? features = null;
        string statePath = Directory.CreateTempSubdirectory("kicad-close-mcp-").FullName;
        string sharedState = Directory.CreateTempSubdirectory("kicad-close-shared-mcp-").FullName;
        try
        {
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            // handled_requests follows the open editors: closing this one removes its request types,
            // the other editor keeps its own, and reopening restores exactly the same list.
            bool pcb = (int)document.Type == 3;
            string kind = pcb ? "pcb" : "schematic";
            string closedEditorType = pcb ? ReadPcbDrcState.Descriptor.FullName : ReadSchematicScreenData.Descriptor.FullName;
            string otherEditorType = pcb ? ReadSchematicScreenData.Descriptor.FullName : ReadPcbDrcState.Descriptor.FullName;
            // One MCP server process serves the board's failed saves and every close below. Only the
            // receipt check after each close starts another, because it proves that a new MCP
            // process reads the receipt KiCad kept.
            await using var mcp = await CancellableMcpClient.StartAsync(sharedState,
                Path.Combine(evidence, $"clean-close-{kind}-{instanceId}.stderr.log"), token);
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            // The checked-batch journey proves the schematic case; this is the board editor's.
            if (pcb) await VerifyRefusedSaveKeepsUnsavedWork(mcp, client, document, otherDocument, instanceId, kind, evidence, token);
            await SaveCheckedThroughMcp(client, document, evidence, token);
            for (int pass = 0; pass < 2; pass++)
            {
                var current = await ObserveLifecycleState(client, document, token);
                Assert.IsFalse(current.NativeContentDirty);
                Assert.AreEqual(current.StateSha256, current.CleanCheckpointSha256);
                var other = await ObserveLifecycleState(client, otherDocument, token);
                var files = current.NativeFiles.ToDictionary(path => path,
                    path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
                var modes = new Dictionary<string, UnixFileMode>();
                string[] reopenedTypes = [];
                if (pass == 1)
                {
                    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux readonly fixture.");
                    foreach (string path in current.NativeFiles)
                    {
                        var mode = File.GetUnixFileMode(path); modes.Add(path, mode);
                        File.SetUnixFileMode(path, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
                    }
                }
                try
                {
                    // Both editors are open here. The first pass proves the list against dispatch.
                    var bothOpenSession = await client.HandshakeAsync(token);
                    features ??= NativeFeatureContracts.Verify(bothOpenSession);
                    CollectionAssert.AreEqual(features, bothOpenSession.Capabilities.ToArray(),
                        $"The feature contracts with both editors open changed before closing the {kind} editor.");
                    string[] bothOpen = pass == 0
                        ? await NativeCapabilityProbe.VerifyHandshakeAsync(client,
                            Path.Combine(evidence, $"{instanceId}-before-{kind}-close-capabilities.json"), token)
                        : bothOpenSession.HandledRequests.ToArray();
                    CollectionAssert.Contains(bothOpen, closedEditorType);
                    CollectionAssert.Contains(bothOpen, otherEditorType);
                    string operation = Guid.NewGuid().ToString("D");
                    LifecycleOperationResult Parse(JsonElement reply) => SchematicJson.Parser.Parse<LifecycleOperationResult>(
                        reply.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
                            .GetProperty("text").GetString()!);
                    LifecycleOperationResult result;
                    {
                        var stale = current.Clone(); stale.Revision.Sequence++;
                        var refused = await mcp.Tool("kicad_document_close", new
                            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(stale), operationId = Guid.NewGuid().ToString("D") });
                        Assert.IsTrue(refused.GetProperty("isError").GetBoolean());
                        Assert.AreEqual(LifecycleOperationStatus.LosRejected, Parse(refused).Status);
                        Assert.AreEqual(current, await ObserveLifecycleState(client, document, token));
                        if (pass == 1)
                        {
                            // Read-only files: a save is refused before anything is written and names every
                            // file, and the editor stays exactly as it was, so the close below still succeeds.
                            var refusedSave = await mcp.Tool("kicad_document_save", new { instanceId,
                                expectedStateJson = SchematicJson.Formatter.Format(current), operationId = Guid.NewGuid().ToString("D") });
                            Assert.IsTrue(refusedSave.GetProperty("isError").GetBoolean(), refusedSave.GetRawText());
                            var failed = Parse(refusedSave);
                            Assert.AreEqual(LifecycleOperationStatus.LosFailed, failed.Status);
                            Assert.AreEqual("file_not_writable", failed.ErrorCode, failed.ErrorMessage);
                            CollectionAssert.AreEquivalent(current.NativeFiles.ToArray(), failed.BlockedFiles.ToArray(), failed.ErrorMessage);
                            Assert.IsEmpty(failed.WrittenFiles, failed.ErrorMessage);
                            Assert.IsFalse(failed.ErrorMessage.Contains("KiCad refused to save", StringComparison.Ordinal), failed.ErrorMessage);
                            StringAssert.Contains(failed.ErrorMessage, "the file is read-only", failed.ErrorMessage);
                            Assert.AreEqual(current, failed.ObservedState);
                            Assert.AreEqual(current, await ObserveLifecycleState(client, document, token));
                            Console.WriteLine($"Read-only {kind} save refused for {instanceId}: {failed.ErrorMessage}");
                        }
                        var request = new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(current), operationId = operation };
                        var reply = await mcp.Tool("kicad_document_close", request);
                        Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
                        result = Parse(reply);
                        Assert.AreEqual(LifecycleOperationStatus.LosClosed, result.Status);
                        Assert.AreEqual(result, Parse(await mcp.Tool("kicad_document_close", request)));
                        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                            client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, token));
                        Assert.AreEqual(other, await ObserveLifecycleState(client, otherDocument, token));
                    }
                    var afterCloseSession = await client.HandshakeAsync(token);
                    CollectionAssert.AreEqual(features, afterCloseSession.Capabilities.ToArray(),
                        $"Closing the {kind} editor must not change the feature contracts: they belong to the build.");
                    string[] afterClose = pass == 0
                        ? await NativeCapabilityProbe.VerifyHandshakeAsync(client,
                            Path.Combine(evidence, $"{instanceId}-after-{kind}-close-capabilities.json"), token)
                        : afterCloseSession.HandledRequests.ToArray();
                    CollectionAssert.IsSubsetOf(afterClose, bothOpen, "Closing an editor must not add request types.");
                    CollectionAssert.DoesNotContain(afterClose, closedEditorType, "The closed editor's request types must leave the handshake.");
                    CollectionAssert.Contains(afterClose, otherEditorType, "The editor that stays open keeps its request types.");
                    Console.WriteLine($"Closing the {kind} editor of {instanceId} removed {bothOpen.Length - afterClose.Length} of {bothOpen.Length} native request types.");
                    reopenedTypes = bothOpen;
                    if (pass == 0) firstPass = (bothOpen, afterClose);
                    await using (var reconnected = await StdioMcpFixture.StartAsync(statePath,
                        Path.Combine(evidence, "clean-close-reconnect-" + operation + ".stderr.log"), token))
                    {
                        var attached = await reconnected.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                        Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                        var receipt = await reconnected.Tool("kicad_document_operation", new
                            { instanceId, documentJson = SchematicJson.Formatter.Format(document), operationId = operation, processEpoch = client.Epoch });
                        Assert.IsFalse(receipt.TryGetProperty("isError", out error) && error.GetBoolean());
                        Assert.AreEqual(result, Parse(receipt));
                    }
                    foreach (var (path, expected) in files)
                    {
                        CollectionAssert.AreEqual(expected.Bytes, await File.ReadAllBytesAsync(path, token));
                        Assert.AreEqual(expected.Written, File.GetLastWriteTimeUtc(path), "Clean close must not write design files.");
                    }
                }
                finally
                {
                    if (OperatingSystem.IsLinux()) foreach (var (path, mode) in modes) File.SetUnixFileMode(path, mode);
                }
                // Reopen through the actual manager so a clean loaded checkpoint is
                // established without another save. The second close is readonly.
                string filename = (int)document.Type == 3 ? document.BoardFilename : document.Project.Name + ".kicad_sch";
                string documentPath = current.NativeFiles.Single(path => Path.GetFileName(path) == filename);
                var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, documentPath, evidence, token, toolName: openTool);
                Assert.AreEqual(document, opened.Document);
                var reopenedSession = await client.HandshakeAsync(token);
                CollectionAssert.AreEqual(reopenedTypes, reopenedSession.HandledRequests.ToArray(),
                    "Reopening the editor must restore exactly the request types it had before closing.");
                CollectionAssert.AreEqual(features, reopenedSession.Capabilities.ToArray(),
                    $"Reopening the {kind} editor must not change the feature contracts: they belong to the build.");
            }
        }
        finally
        {
            Directory.Delete(statePath, true);
            Directory.Delete(sharedState, true);
        }
        return (firstPass.BothOpen, firstPass.AfterClose, features!);
    }

    // The board editor holds an unsaved edit. With every document file read-only, a save through the
    // MCP server is refused before anything is written and names every file. With a full disk (strace
    // makes every flush of KiCad's main thread fail with ENOSPC), the board write fails and KiCad
    // reports the exact system error. Beside one instance, a board of a project KiCad opened
    // read-only is refused as well. Each time the editor keeps exactly the unsaved edit, and the
    // schematic open in the same KiCad and the other KiCad instance stay unchanged. The edit is then
    // undone by restoring the title block, so the fixture stays as it was.
    private static async Task VerifyRefusedSaveKeepsUnsavedWork(CancellableMcpClient mcp, NativeClient client,
        DocumentSpecifier document, DocumentSpecifier otherDocument, string instanceId, string kind, string evidence,
        CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux file modes and process tracing.");
        var sibling = await SiblingInstance(client, token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = sibling.Endpoint, expectedInstanceId = sibling.InstanceId }));
        string siblingBefore = await SiblingSnapshot(mcp, sibling, token);
        var otherBefore = await ObserveLifecycleState(client, otherDocument, token);
        var otherDisk = otherBefore.NativeFiles.ToDictionary(path => path, path => File.ReadAllBytes(path));
        var original = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        var edited = original.Clone();
        edited.Comment9 = $"Unsaved {kind} work {Guid.NewGuid():N}";
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = edited }, token);
        var dirty = await ObserveLifecycleState(client, document, token);
        Assert.IsTrue(dirty.NativeContentDirty, $"The {kind} edit must be unsaved work in the editor.");
        var disk = dirty.NativeFiles.ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        string board = dirty.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pcb");
        var reportedCodes = new SortedSet<string>(StringComparer.Ordinal);

        // The editor keeps exactly the unsaved edit, and no document file changed.
        async Task<DocumentLifecycleState> WorkKept(string phase)
        {
            var state = await ObserveLifecycleState(client, document, token);
            Assert.IsTrue(state.NativeContentDirty, $"{phase}: the {kind} editor lost its unsaved state.");
            Assert.AreEqual(edited.Comment9, (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = document }, token)).Comment9, $"{phase}: the unsaved {kind} edit was lost.");
            foreach (var (path, before) in disk)
            {
                CollectionAssert.AreEqual(before.Bytes, await File.ReadAllBytesAsync(path, token), $"{phase}: {path} changed on disk.");
                Assert.AreEqual(before.Written, File.GetLastWriteTimeUtc(path), $"{phase}: {path} was rewritten by a failed save.");
            }
            return state;
        }
        Task<JsonElement> Save(DocumentLifecycleState expected) => mcp.Tool("kicad_document_save", new { instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(expected), operationId = Guid.NewGuid().ToString("D") });
        async Task<LifecycleOperationResult> FailedSave(string phase, JsonElement reply, string code = "file_not_writable")
        {
            Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), $"{phase}: a failed {kind} save must not report success. {reply.GetRawText()}");
            var failed = LifecycleResult(reply);
            Assert.AreEqual(LifecycleOperationStatus.LosFailed, failed.Status, failed.ErrorMessage);
            Assert.AreEqual(code, failed.ErrorCode, failed.ErrorMessage);
            Assert.IsEmpty(failed.WrittenFiles, failed.ErrorMessage);
            StringAssert.Contains(failed.ErrorMessage, "KiCad replaced none of the document's files.", failed.ErrorMessage);
            StringAssert.Contains(failed.ErrorMessage, "The editor still holds all unsaved changes.", failed.ErrorMessage);
            Assert.IsFalse(failed.ErrorMessage.Contains("KiCad refused to save", StringComparison.Ordinal), failed.ErrorMessage);
            Assert.AreEqual(await WorkKept(phase), failed.ObservedState, $"{phase}: the receipt shows the editor after the failure.");
            await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-{kind}-save-{phase}.json"), SchematicJson.Formatter.Format(failed), token);
            Console.WriteLine($"{phase} {kind} save with unsaved work failed for {instanceId}: {failed.ErrorMessage}");
            reportedCodes.Add(failed.ErrorCode);
            return failed;
        }

        var modes = new Dictionary<string, UnixFileMode>();
        try
        {
            foreach (string path in dirty.NativeFiles) KeepReadOnly(path, modes);
            var readOnly = await FailedSave("read-only", await Save(dirty));
            CollectionAssert.AreEquivalent(dirty.NativeFiles.ToArray(), readOnly.BlockedFiles.ToArray(), readOnly.ErrorMessage);
            Assert.AreEqual(dirty, readOnly.ObservedState, "The receipt shows the editor after the refusal.");
        }
        finally { RestoreModes(modes); }

        // One save under a full disk: strace makes flushes of KiCad's main thread fail with ENOSPC.
        int kicad = FixtureProcessId(client);
        async Task<(LifecycleOperationResult Result, string[] Injected)> FullDisk(string phase, string rule, string code)
        {
            var expected = await WorkKept(phase + " (before)");
            JsonElement reply;
            string[] trace;
            await using (var fault = await SyscallFault.AttachAsync(kicad, Path.Combine(evidence, $"{instanceId}-{kind}-{phase}.strace"),
                token, "-e", "trace=fsync", "-e", rule))
            {
                try { reply = await Save(expected); }
                finally { trace = await fault.DetachAsync(); }
            }
            return (await FailedSave(phase, reply, code), SyscallFault.Injected(trace));
        }

        // The board file cannot be flushed: KiCad reports the exact system error for it.
        var (full, injected) = await FullDisk("full-disk", "inject=fsync:error=ENOSPC", "file_not_writable");
        CollectionAssert.AreEqual(new[] { board }, full.BlockedFiles.ToArray(), full.ErrorMessage);
        StringAssert.Contains(full.ErrorMessage, "No space left on device", "KiCad reports the exact system error.");
        StringAssert.Contains(full.ErrorMessage, "free disk space", full.ErrorMessage);
        Assert.IsTrue(injected.Any(line => line.Contains(Path.GetFileName(board) + ".kicad-save-", StringComparison.Ordinal)),
            "The full disk stopped the board file's flush. " + string.Join(" | ", injected));

        // A design rule edit is also project settings, which the board editor writes first. Their
        // writer keeps the system error to itself and the board editor reports only its own
        // message, so the checked save names the file it saw KiCad begin to replace and leave
        // unchanged, finds nothing that blocks it now, and never calls it blocked:
        // native_save_failed, with nothing written and KiCad's own message quoted.
        var rules = await client.InvokeAsync<GetBoardDesignRules, BoardDesignRulesResponse>(new() { Board = document }, token);
        var widened = new Kiapi.Board.BoardDesignRules { Constraints = rules.Rules.Constraints.Clone() };
        widened.Constraints.MinClearance.ValueNm += 1000;
        await client.InvokeAsync<SetBoardDesignRules, BoardDesignRulesResponse>(new() { Board = document, Rules = widened }, token);
        try
        {
            var (settings, settingsFaults) = await FullDisk("project-settings-write", "inject=fsync:error=ENOSPC:when=1", "native_save_failed");
            string project = dirty.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pro");
            Assert.HasCount(1, settingsFaults);
            StringAssert.Contains(settingsFaults[0], Path.GetFileName(project) + ".kicad-save-", "The fault hit the project file's flush.");
            Assert.IsEmpty(settings.BlockedFiles, "A failure without a found reason never marks a file blocked. " + settings.ErrorMessage);
            foreach (string text in new[] { "KiCad reported: PCB persistence failed",
                         "KiCad could not write '" + Path.GetFileName(project) + "'", "while KiCad was replacing this file",
                         "gave no system reason", "check the disk" })
                StringAssert.Contains(settings.ErrorMessage, text, settings.ErrorMessage);
            Assert.IsFalse(settings.ErrorMessage.Contains("make the file or folder writable", StringComparison.Ordinal), settings.ErrorMessage);
        }
        finally
        {
            var restored = new Kiapi.Board.BoardDesignRules { Constraints = rules.Rules.Constraints.Clone() };
            await client.InvokeAsync<SetBoardDesignRules, BoardDesignRulesResponse>(new() { Board = document, Rules = restored }, token);
        }
        Assert.AreEqual(rules.Rules.Constraints, (await client.InvokeAsync<GetBoardDesignRules, BoardDesignRulesResponse>(
            new() { Board = document }, token)).Rules.Constraints, "The design rules are restored.");

        // A board save in a project KiCad opened read-only needs a KiCad process of its own, so it
        // runs beside one of the two fixture instances (the one whose socket sorts first).
        bool readOnlyProject = string.CompareOrdinal(client.Endpoint, sibling.Endpoint) < 0;
        if (readOnlyProject)
            await VerifyReadOnlyProjectBoardSave(client, dirty, instanceId, evidence, reportedCodes, token);
        RequireExactSaveFailureCodes("board", instanceId,
            readOnlyProject ? BoardSaveFailureCodes.Concat(ReadOnlyProjectBoardSaveFailureCodes) : BoardSaveFailureCodes, reportedCodes);

        Assert.AreEqual(otherBefore, await ObserveLifecycleState(client, otherDocument, token), $"Failed {kind} saves must leave the other open document unchanged.");
        foreach (var (path, bytes) in otherDisk)
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token), path + " of the other document changed on disk.");
        Assert.AreEqual(siblingBefore, await SiblingSnapshot(mcp, sibling, token), $"Failed {kind} saves must leave the other KiCad instance untouched.");
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = original }, token);
    }

    // A board in a project whose file was read-only when KiCad opened it. KiCad keeps the project
    // read-only after the file is writable again, so the board editor refuses the save with that
    // reason (native_save_refused): no file is called blocked because every file is writable,
    // nothing is written, and the editor keeps the unsaved edit. It runs in a KiCad started through
    // the MCP server on a copy of this instance's saved board and project file; the fixture's own
    // files are only read.
    private static async Task VerifyReadOnlyProjectBoardSave(NativeClient client, DocumentLifecycleState saved,
        string instanceId, string evidence, ISet<string> reportedCodes, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux process environment and file modes.");
        string settings = Directory.CreateTempSubdirectory("kicad-readonly-board-settings-").FullName;
        string copy = Directory.CreateTempSubdirectory("kicad-readonly-board-project-").FullName;
        string state = Directory.CreateTempSubdirectory("kicad-readonly-board-mcp-").FullName;
        var modes = new Dictionary<string, UnixFileMode>();
        string? started = null;
        try
        {
            var (executable, environment) = await FixtureKiCadLaunch(FixtureProcessId(client), settings, token);
            await using var mcp = await CancellableMcpClient.StartAsync(state,
                Path.Combine(evidence, instanceId + "-read-only-board-mcp.stderr.log"), token, environment);
            string board = Path.Combine(copy, Path.GetFileName(saved.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pcb")));
            string project = Path.Combine(copy, Path.GetFileName(saved.NativeFiles.Single(path => Path.GetExtension(path) == ".kicad_pro")));
            foreach (string file in saved.NativeFiles) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            KeepReadOnly(project, modes);
            var clock = Stopwatch.StartNew();
            var start = await mcp.Tool("kicad_instance_start", new { executable, projectPath = project });
            RequireToolSuccess(start);
            started = start.GetProperty("structuredContent").GetProperty("instanceId").GetString()!;
            Console.WriteLine($"KiCad {started} started on the read-only project copy after {clock.Elapsed.TotalSeconds:F1}s.");
            RestoreModes(modes);

            var opened = await mcp.Tool("kicad_pcb_open", new { instanceId = started, path = board });
            RequireToolSuccess(opened);
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(opened.GetProperty("content").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            var editor = new NativeClient(new NngTransport(),
                NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(started), "api.sock")));
            var title = await editor.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
            title.Comment9 = "Unsaved read-only project board work " + Guid.NewGuid().ToString("N");
            await editor.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = title }, token);
            var dirty = await ObserveLifecycleState(editor, document, token);
            Assert.IsTrue(dirty.NativeContentDirty, "The board edit must be unsaved work in the editor.");
            CollectionAssert.AreEquivalent(new[] { board, project }, dirty.NativeFiles.ToArray(), "The copied board and project file are the document.");
            var disk = dirty.NativeFiles.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));

            var reply = await mcp.Tool("kicad_document_save", new { instanceId = started,
                expectedStateJson = SchematicJson.Formatter.Format(dirty), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), "A refused board save must not report success. " + reply.GetRawText());
            var result = LifecycleResult(reply);
            Assert.AreEqual(LifecycleOperationStatus.LosFailed, result.Status, result.ErrorMessage);
            Assert.AreEqual("native_save_refused", result.ErrorCode, result.ErrorMessage);
            Assert.IsEmpty(result.BlockedFiles, "Every file is writable; KiCad's own state refuses the save. " + result.ErrorMessage);
            Assert.IsEmpty(result.WrittenFiles, result.ErrorMessage);
            foreach (string text in new[] { "KiCad refused to save", Path.GetFileName(project),
                         "read-only because its project file was read-only when the project was opened", "the file is writable now",
                         "reopen the project in KiCad", "making the document's files writable does not help",
                         "KiCad replaced none of the document's files.", "The editor still holds all unsaved changes." })
                StringAssert.Contains(result.ErrorMessage, text, result.ErrorMessage);
            // The refusal is the explanation, not KiCad's generic failure or a guessed file problem.
            foreach (string text in new[] { "KiCad reported", "KiCad cannot write", "KiCad could not write", "project lock" })
                Assert.IsFalse(result.ErrorMessage.Contains(text, StringComparison.Ordinal), $"Must not say '{text}'. {result.ErrorMessage}");
            Assert.AreEqual(dirty, result.ObservedState, "The receipt shows the editor after the refusal.");
            Assert.AreEqual(dirty, await ObserveLifecycleState(editor, document, token), "The board editor state changed.");
            Assert.AreEqual(title.Comment9, (await editor.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = document }, token)).Comment9, "The unsaved board edit was lost.");
            foreach (var (path, before) in disk)
            {
                CollectionAssert.AreEqual(before.Bytes, await File.ReadAllBytesAsync(path, token), path + " changed on disk.");
                Assert.AreEqual(before.Written, File.GetLastWriteTimeUtc(path), path + " was rewritten by a refused save.");
            }
            reportedCodes.Add(result.ErrorCode);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-save-read-only-project.json"),
                SchematicJson.Formatter.Format(result), token);
            Console.WriteLine($"Board save refused (project opened read-only) in KiCad {started}: {result.ErrorMessage}");
        }
        finally
        {
            RestoreModes(modes);
            if (started is not null)
            {
                await StopStartedKiCad(started);
                string runtime = NativeIpcEndpoint.RuntimeDirectory(started);
                if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
            }
            foreach (string folder in new[] { settings, copy, state }) Directory.Delete(folder, true);
        }
    }

    // An agent cancels a close while KiCad is not answering. KiCad never receives it: the editor
    // stays open with the same saved document, no file is written, and the receipt query says so.
    private static async Task VerifyCancelledCloseKeepsEditor(CancellableMcpClient mcp, NativeClient client,
        DocumentSpecifier document, int processId, string evidence, string instanceId, CancellationToken token)
    {
        var clean = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(clean.NativeContentDirty);
        Assert.AreEqual(clean.StateSha256, clean.CleanCheckpointSha256, "A close needs a verified clean checkpoint.");
        var files = clean.NativeFiles.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        string operation = Guid.NewGuid().ToString("D");
        var reply = await CancelWhileKiCadIsStopped(mcp, processId, "kicad_document_close",
            new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(clean), operationId = operation }, token);
        await OperationNotStarted(mcp, client, document, instanceId, operation, token);
        Assert.AreEqual(clean, await ObserveLifecycleState(client, document, token), "The editor must stay open and unchanged.");
        var open = await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = document.Type }, token);
        CollectionAssert.Contains(open.Documents.ToArray(), document, "The cancelled close must leave the document open.");
        foreach (var (path, before) in files)
        {
            CollectionAssert.AreEqual(before.Bytes, await File.ReadAllBytesAsync(path, token), path + " changed on disk.");
            Assert.AreEqual(before.Written, File.GetLastWriteTimeUtc(path), path + " was written by a cancelled close.");
        }
        NotReportedAsSuccess(reply, "cancelled close");
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-cancelled-close.json"), SchematicJson.Formatter.Format(clean), token);
        Console.WriteLine($"Cancelled close of {instanceId} left the editor open; KiCad has no record of starting operation {operation}.");
    }
}
