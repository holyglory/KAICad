using System.Diagnostics;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("NativeFootprintLibrary")]
public sealed class NativeFootprintLibraryJourney
{
    [TestMethod]
    public async Task FootprintLibraryListDeleteUsesLiveEditorAndNativeReceipts()
    {
        Assert.IsTrue(OperatingSystem.IsLinux());
        DirectoryInfo source = FindRoot();
        string executable = Path.Combine(source.FullName, "automation/artifacts/native/kicad/kicad");
        Assert.IsTrue(File.Exists(executable), executable);
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(source.FullName, "automation/artifacts/native-footprint-library"));
        string scratch = Directory.CreateTempSubdirectory("kicad-footprint-library-").FullName;
        var (displayProcess, displayNumber) = await StartDisplay();
        string display = ":" + displayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string projectDirectory = Directory.CreateDirectory(Path.Combine(scratch, "project")).FullName;
        string project = Path.Combine(projectDirectory, "fixture.kicad_pro");
        string library = Directory.CreateDirectory(Path.Combine(projectDirectory, "Fixture.pretty")).FullName;
        string sourceFootprint = Path.Combine(source.FullName, "qa/data/libraries/Resistor_SMD.pretty/R_0201_0603Metric.kicad_mod");
        string footprintName = "Generated_Test";
        string fixtureFootprint = Path.Combine(library, footprintName + ".kicad_mod");
        string richDefinition = await File.ReadAllTextAsync(sourceFootprint);
        richDefinition = richDefinition.Replace("\t(attr smd)\n", "\t(attr smd)\n\t(solder_mask_margin 0.01)\n\t(solder_paste_margin_ratio 0.15)\n\t(property \"FixtureProvenance\" \"rich\"\n\t\t(at 0 0 0)\n\t\t(layer \"F.Fab\")\n\t\t(hide yes)\n\t\t(uuid \"8b80d985-95fd-4c20-9fe8-b2a9e8733e60\")\n\t\t(effects (font (size 1.27 1.27)))\n\t)\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixtureFootprint, richDefinition);
        await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "fp-lib-table"),
            $"(fp_lib_table (version 7) (lib (name \"Fixture\") (type \"KiCad\") (uri \"{library.Replace("\\", "/", StringComparison.Ordinal)}\") (options \"\") (descr \"\")))\n");
        string instanceId = Guid.NewGuid().ToString("D"), socket = Path.Combine(projectDirectory, "api.sock");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = projectDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        process.StartInfo.Environment["DISPLAY"] = display;
        process.StartInfo.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
        process.StartInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(projectDirectory, "config");
        process.StartInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(projectDirectory, "cache");
        foreach (string arg in new[] { "--automation", instanceId, "--api-socket", socket,
                                       "--automation-log", Path.Combine(evidence, "footprint-editor.log"),
                                       "--software-rendering", project })
            process.StartInfo.ArgumentList.Add(arg);
        Assert.IsTrue(process.Start());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var registry = new InstanceRegistry(new NngTransport(), Path.Combine(scratch, "registry"));
        try
        {
            int backoff = 25;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if( process.HasExited )
                {
                    string stdout = await process.StandardOutput.ReadToEndAsync(deadline.Token);
                    string stderr = await process.StandardError.ReadToEndAsync(deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-editor-startup.stdout.log"), stdout, deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-editor-startup.stderr.log"), stderr, deadline.Token);
                    Assert.Fail($"Footprint editor exited during startup. {stderr}");
                }
                try { await registry.AttachAsync("ipc://" + socket, instanceId, deadline.Token); break; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(backoff, deadline.Token); backoff = Math.Min(backoff * 2, 500);
            }
            Assert.IsTrue(NativeKeyboard.HasWindow(display, process.Id, "KiCad"), "KiCad manager window did not become visible.");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "manager-before-footprint-editor.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(display, process.Id, "click", "KiCad", false, true,
                clickFromLeft: 330, clickFromTop: 230);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "manager-after-footprint-editor-key.png"), deadline.Token);
            using var editorWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            editorWait.CancelAfter(TimeSpan.FromSeconds(20));
            while (!NativeKeyboard.HasWindow(display, process.Id, "Footprint Editor"))
                await Task.Delay(100, editorWait.Token);
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(scratch, "mcp"), Path.Combine(evidence, "footprint-mcp.stderr.log"), deadline.Token);
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = "ipc://" + socket, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean(), attached.GetRawText());
            string epoch = registry.Get(instanceId).Epoch;

            var listed = await mcp.Tool("kicad_footprint_library_list", new { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture" });
            Assert.IsFalse(listed.TryGetProperty("isError", out var listError) && listError.GetBoolean(), listed.GetRawText());
            Assert.IsTrue(listed.GetProperty("structuredContent").GetProperty("footprintNames").EnumerateArray().Any(item => item.GetString() == footprintName), listed.GetRawText());

            string documentJson = "{\"type\":\"DOCTYPE_FOOTPRINT\",\"libId\":{\"libraryNickname\":\"Fixture\",\"entryName\":\"Generated_Test\"}}";
            var definitionBeforeRewrite = await mcp.Tool("kicad_footprint_definition_read", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, source = "persisted_library" });
            Assert.IsFalse(definitionBeforeRewrite.TryGetProperty("isError", out var definitionBeforeError) && definitionBeforeError.GetBoolean(), definitionBeforeRewrite.GetRawText());
            var definitionBeforeData = definitionBeforeRewrite.GetProperty("structuredContent");
            Assert.IsTrue(definitionBeforeData.GetProperty("definitionComplete").GetBoolean(), definitionBeforeRewrite.GetRawText());
            Assert.AreEqual("NFBS_UNCHANGED", definitionBeforeData.GetProperty("fileBaseline").GetProperty("status").GetString(), definitionBeforeRewrite.GetRawText());
            Assert.IsTrue(definitionBeforeData.GetProperty("footprint").GetProperty("definition").GetProperty("referenceField").GetProperty("text").GetProperty("text").GetString() is not null, definitionBeforeRewrite.GetRawText());
            Assert.IsTrue(definitionBeforeData.GetProperty("footprint").GetProperty("definition").GetProperty("items").GetArrayLength() > 0, definitionBeforeRewrite.GetRawText());
            string originalRichBytes = await File.ReadAllTextAsync(fixtureFootprint);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(fixtureFootprint);
            string rewrittenRichBytes = originalRichBytes.Replace("Resistor SMD 0201", "Resistor SMD 0202", StringComparison.Ordinal);
            Assert.AreEqual(originalRichBytes.Length, rewrittenRichBytes.Length, "The cache regression must keep the fixture byte size unchanged.");
            await File.WriteAllTextAsync(fixtureFootprint, rewrittenRichBytes);
            File.SetLastWriteTimeUtc(fixtureFootprint, originalWriteTime);
            var definitionAfterRewrite = await mcp.Tool("kicad_footprint_definition_read", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, source = "persisted_library" });
            Assert.IsFalse(definitionAfterRewrite.TryGetProperty("isError", out var definitionAfterError) && definitionAfterError.GetBoolean(), definitionAfterRewrite.GetRawText());
            Assert.AreNotEqual(definitionBeforeData.GetProperty("canonicalSha256").GetString(),
                definitionAfterRewrite.GetProperty("structuredContent").GetProperty("canonicalSha256").GetString(), definitionAfterRewrite.GetRawText());
            var opened = await mcp.Tool("kicad_footprint_open_library_item", new
            { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture", entryName = footprintName,
                operationId = Guid.NewGuid().ToString("D") });
            if (opened.TryGetProperty("isError", out var openError) && openError.GetBoolean())
            {
                await CaptureOpenFailure(process.Id, display, evidence, opened.GetRawText());
                // OpenLibraryItem has no native operation receipt. Reconcile a lost
                // reply by observing the editor before considering the open failed.
                Assert.AreEqual("operation_outcome_unknown", opened.GetProperty("structuredContent").GetProperty("errorCode").GetString(), opened.GetRawText());
                var openedAfterLostReply = await mcp.Tool("kicad_footprint_open_documents", new { instanceId, expectedProcessEpoch = epoch });
                Assert.IsFalse(openedAfterLostReply.TryGetProperty("isError", out var openedAfterLostReplyError)
                    && openedAfterLostReplyError.GetBoolean(), openedAfterLostReply.GetRawText());
                Assert.IsTrue(openedAfterLostReply.GetProperty("structuredContent").GetProperty("response")
                    .GetProperty("documents").EnumerateArray()
                    .Any(item => item.GetProperty("libId").GetProperty("entryName").GetString() == footprintName), openedAfterLostReply.GetRawText());
            }

            var saved = await mcp.Tool("kicad_footprint_save", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(saved.TryGetProperty("isError", out var saveError) && saveError.GetBoolean(), saved.GetRawText());
            var saveData = saved.GetProperty("structuredContent");
            Assert.IsTrue(saveData.GetProperty("nativeCommandSucceeded").GetBoolean(), saved.GetRawText());
            Assert.IsTrue(saveData.GetProperty("libraryEntryObserved").GetBoolean(), saved.GetRawText());
            Assert.IsTrue(saveData.GetProperty("geometryPersistenceVerified").GetBoolean(), saved.GetRawText());
            Assert.IsTrue(saveData.GetProperty("persistenceVerified").GetBoolean(), saved.GetRawText());
            Assert.AreEqual("native-forced-refresh-definition-readback", saveData.GetProperty("persistenceSource").GetString(), saved.GetRawText());
            Assert.IsTrue(saveData.GetProperty("definitionComplete").GetBoolean(), saved.GetRawText());
            Assert.AreEqual("NFBS_UNCHANGED", saveData.GetProperty("fileBaseline").GetProperty("status").GetString(), saved.GetRawText());

            string copyName = "Generated_Test_Copy";
            var copied = await mcp.Tool("kicad_footprint_save_copy", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, path = copyName,
                operationId = Guid.NewGuid().ToString("D"), overwrite = false, includeProject = false });
            Assert.IsFalse(copied.TryGetProperty("isError", out var copyError) && copyError.GetBoolean(), copied.GetRawText());
            var copyData = copied.GetProperty("structuredContent");
            Assert.IsTrue(copyData.GetProperty("nativeCommandSucceeded").GetBoolean(), copied.GetRawText());
            Assert.IsTrue(copyData.GetProperty("libraryEntryObserved").GetBoolean(), copied.GetRawText());
            Assert.IsTrue(copyData.GetProperty("geometryPersistenceVerified").GetBoolean(), copied.GetRawText());
            Assert.IsTrue(copyData.GetProperty("persistenceVerified").GetBoolean(), copied.GetRawText());
            Assert.AreEqual("native-forced-refresh-definition-readback", copyData.GetProperty("persistenceSource").GetString(), copied.GetRawText());
            Assert.IsTrue(copyData.GetProperty("definitionComplete").GetBoolean(), copied.GetRawText());
            Assert.AreEqual("NFBS_UNCHANGED", copyData.GetProperty("fileBaseline").GetProperty("status").GetString(), copied.GetRawText());
            Assert.AreEqual("Fixture", copyData.GetProperty("persistedLibraryNickname").GetString(), copied.GetRawText());
            Assert.AreEqual(copyName, copyData.GetProperty("persistedFootprintName").GetString(), copied.GetRawText());

            var refusedOverwrite = await mcp.Tool("kicad_footprint_save_copy", new
            { instanceId, expectedProcessEpoch = epoch, documentJson, path = copyName,
                operationId = Guid.NewGuid().ToString("D"), overwrite = false, includeProject = false });
            Assert.IsTrue(refusedOverwrite.TryGetProperty("isError", out var refusedOverwriteError)
                && refusedOverwriteError.GetBoolean(), refusedOverwrite.GetRawText());

            var afterCopy = await mcp.Tool("kicad_footprint_library_list", new { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture" });
            Assert.IsFalse(afterCopy.TryGetProperty("isError", out var afterCopyError) && afterCopyError.GetBoolean(), afterCopy.GetRawText());
            Assert.IsTrue(afterCopy.GetProperty("structuredContent").GetProperty("footprintNames").EnumerateArray()
                .Any(item => item.GetString() == copyName), afterCopy.GetRawText());

            var reopened = await mcp.Tool("kicad_footprint_open_library_item", new
            { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture", entryName = copyName,
                operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(reopened.TryGetProperty("isError", out var reopenError) && reopenError.GetBoolean(), reopened.GetRawText());
            Assert.AreEqual(copyData.GetProperty("snapshotSha256").GetString(),
                reopened.GetProperty("structuredContent").GetProperty("snapshotSha256").GetString(), reopened.GetRawText());
            var openDocuments = await mcp.Tool("kicad_footprint_open_documents", new { instanceId, expectedProcessEpoch = epoch });
            Assert.IsFalse(openDocuments.TryGetProperty("isError", out var openDocumentsError) && openDocumentsError.GetBoolean(), openDocuments.GetRawText());
            Assert.AreEqual(1, openDocuments.GetProperty("structuredContent").GetProperty("usableDocumentCount").GetInt32(), openDocuments.GetRawText());
            Assert.AreEqual(copyName, openDocuments.GetProperty("structuredContent").GetProperty("response")
                .GetProperty("documents")[0].GetProperty("libId").GetProperty("entryName").GetString(), openDocuments.GetRawText());

            string deleteOperation = Guid.NewGuid().ToString("D");
            var deleted = await mcp.Tool("kicad_footprint_library_delete", new
            { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture", footprintName, operationId = deleteOperation,
                expectedModifyHash = afterCopy.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.IsFalse(deleted.TryGetProperty("isError", out var deleteError) && deleteError.GetBoolean(), deleted.GetRawText());
            Assert.IsTrue(deleted.GetProperty("structuredContent").GetProperty("nativeReceiptAvailable").GetBoolean(), deleted.GetRawText());
            var afterDelete = await mcp.Tool("kicad_footprint_library_list", new { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture" });
            Assert.IsFalse(afterDelete.GetProperty("structuredContent").GetProperty("footprintNames").EnumerateArray().Any(item => item.GetString() == footprintName), afterDelete.GetRawText());
            var staleDelete = await mcp.Tool("kicad_footprint_library_delete", new
            { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture", footprintName,
                operationId = Guid.NewGuid().ToString("D"),
                expectedModifyHash = listed.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.IsTrue(staleDelete.TryGetProperty("isError", out var staleDeleteError) && staleDeleteError.GetBoolean(), staleDelete.GetRawText());

            var deleteReplay = await mcp.Tool("kicad_footprint_library_delete", new
            { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture", footprintName, operationId = deleteOperation,
                expectedModifyHash = afterCopy.GetProperty("structuredContent").GetProperty("modifyHash").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.AreEqual(deleted.GetProperty("structuredContent").GetRawText(), deleteReplay.GetProperty("structuredContent").GetRawText());

            var changedAdmissionReplay = await mcp.Tool("kicad_footprint_library_delete", new
            { instanceId, expectedProcessEpoch = epoch, libraryNickname = "Fixture", footprintName, operationId = deleteOperation,
                expectedModifyHash = "1" });
            Assert.IsTrue(changedAdmissionReplay.TryGetProperty("isError", out var changedAdmissionError)
                && changedAdmissionError.GetBoolean(), changedAdmissionReplay.GetRawText());
            Assert.AreEqual("operation_id_reused", changedAdmissionReplay.GetProperty("structuredContent").GetProperty("errorCode").GetString());

            await using var reconnectMcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(scratch, "mcp-reconnect"), Path.Combine(evidence, "footprint-mcp-reconnect.stderr.log"), deadline.Token);
            var reconnectAttached = await reconnectMcp.Tool("kicad_instance_attach", new { endpoint = "ipc://" + socket, expectedInstanceId = instanceId });
            Assert.IsFalse(reconnectAttached.TryGetProperty("isError", out var reconnectAttachError) && reconnectAttachError.GetBoolean(), reconnectAttached.GetRawText());
            var nativeReceiptAfterReconnect = await reconnectMcp.Tool("kicad_footprint_receipt", new
            { instanceId, expectedProcessEpoch = epoch, operationId = deleteOperation });
            Assert.IsFalse(nativeReceiptAfterReconnect.TryGetProperty("isError", out var nativeReceiptError) && nativeReceiptError.GetBoolean(), nativeReceiptAfterReconnect.GetRawText());
            Assert.IsTrue(nativeReceiptAfterReconnect.GetProperty("structuredContent").GetProperty("found").GetBoolean(), nativeReceiptAfterReconnect.GetRawText());
            Assert.AreEqual("native-process", nativeReceiptAfterReconnect.GetProperty("structuredContent").GetProperty("receiptScope").GetString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            if (!displayProcess.HasExited) displayProcess.Kill(true);
            await displayProcess.WaitForExitAsync();
            Directory.Delete(scratch, true);
        }
    }

    private static async Task CaptureOpenFailure(int processId, string display, string evidence, string response)
    {
        await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-open-failure.json"), response);
        var windows = new List<string>();
        NativeKeyboard.HasWindow(display, processId, "Footprint Editor", describe: windows.Add);
        await File.WriteAllLinesAsync(Path.Combine(evidence, "footprint-open-failure-windows.txt"), windows);

        // Read-only diagnosis of this fixture-owned process. A modal wx assertion can
        // block the native API before it returns a result; preserve its actual stack
        // without dismissing the assertion or changing the acceptance result.
        var start = new ProcessStartInfo("gdb")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--batch", "--nx", "--quiet", "--readnever",
            "-iex", "set debuginfod enabled off", "-iex", "set auto-solib-add off", "-p",
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ex", "set pagination off",
            "-ex", "sharedlibrary .*_pcbnew.*", "-ex", "thread 1", "-ex", "bt 50", "-ex", "detach" })
            start.ArgumentList.Add(argument);

        try
        {
            using var debugger = Process.Start(start)!;
            Task<string> stdout = debugger.StandardOutput.ReadToEndAsync();
            Task<string> stderr = debugger.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await debugger.WaitForExitAsync(limit.Token); }
            finally
            {
                if (!debugger.HasExited) { debugger.Kill(); await debugger.WaitForExitAsync(); }
                await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-open-failure-stack.stdout.log"), await stdout);
                await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-open-failure-stack.stderr.log"), await stderr);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "footprint-open-failure-stack-unavailable.txt"), error.ToString());
        }
    }

    private static DirectoryInfo FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "automation/KiCad.Automation.slnx"))) current = current.Parent;
        return current ?? throw new InvalidOperationException("KiCad repository root was not found.");
    }

    private static async Task<(Process Process, int Number)> StartDisplay()
    {
        var start = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" }) start.ArgumentList.Add(arg);
        Process display = Process.Start(start)!;
        string? output = await display.StandardOutput.ReadLineAsync();
        Assert.IsTrue(int.TryParse(output, out int number));
        _ = display.StandardError.ReadToEndAsync();
        return (display, number);
    }
}
