using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Rendered ERC override paths, not complete schematic/settings revision
// coverage or a substitute for the frozen native build receipt.
[TestClass, TestCategory("ExternalIntegration"), TestCategory("NativeErcDialog")]
public sealed class NativeErcDialogTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ActualErcOverridesPreserveCancelledAndUnchangedEditsAndRejectStaleRequests()
    {
        Assert.IsTrue(OperatingSystem.IsLinux(), "This is a Linux native-display fixture, not Mac evidence.");
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "automation/KiCad.Automation.slnx")))
            source = source.Parent;
        Assert.IsNotNull(source);
        string executable = Environment.GetEnvironmentVariable("KICAD_ERC_NATIVE_EXECUTABLE")
            ?? Path.Combine(source.FullName, "automation/artifacts/native/kicad/kicad");
        Assert.IsTrue(Path.IsPathFullyQualified(executable) && File.Exists(executable));
        // Retain this run, not every older TRX attachment tree. The same
        // current/history helper preserves prior evidence without deleting it.
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(source.FullName, "automation/artifacts/native-erc-evidence"));
        string scratch = Directory.CreateTempSubdirectory("kicad-erc-").FullName;
        // The original sequence took about 14 s; undo and redo of every edit add about
        // twenty rendered steps, each bounded by its own 10 s wait.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var processes = new List<Process>();
        var logs = new List<Task>();
        string? display = null;
        try
        {
            var startDisplay = new ProcessStartInfo("Xvfb")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
                startDisplay.ArgumentList.Add(argument);
            var displayProcess = Process.Start(startDisplay)!;
            processes.Add(displayProcess);
            logs.Add(Capture(displayProcess.StandardError, Path.Combine(evidence, "display.stderr.log")));
            string? number = await displayProcess.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.IsTrue(int.TryParse(number, out _), "Xvfb must allocate its own display.");
            display = ":" + number;
            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(scratch, "registry"));
            var identities = new List<ErcDesign>();

            for (int index = 0; index < 2; ++index)
            {
                string directory = Directory.CreateDirectory(Path.Combine(scratch, index.ToString())).FullName;
                string project = Path.Combine(directory, "erc.kicad_pro");
                string schematic = Path.ChangeExtension(project, ".kicad_sch");
                await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
                File.Copy(Path.Combine(source.FullName, "qa/data/eeschema/erc_pin_not_connected_basic.kicad_sch"), schematic);
                string instanceId = Guid.NewGuid().ToString("D");
                string endpoint = Path.Combine(directory, "api.sock");
                var start = new ProcessStartInfo(executable)
                { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["DISPLAY"] = display;
                start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
                start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
                start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
                foreach (string argument in new[] { "--automation", instanceId, "--api-socket", endpoint,
                    "--automation-log", Path.Combine(evidence, $"{index}-native.log"), "--software-rendering", project })
                    start.ArgumentList.Add(argument);
                var process = Process.Start(start)!;
                processes.Add(process);
                logs.Add(Capture(process.StandardOutput, Path.Combine(evidence, $"{index}-stdout.log")));
                logs.Add(Capture(process.StandardError, Path.Combine(evidence, $"{index}-stderr.log")));
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                startup.CancelAfter(TimeSpan.FromSeconds(20));
                int backoff = 25;
                while (true)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    Assert.IsFalse(process.HasExited, "The isolated editor exited during startup.");
                    try { await registry.AttachAsync("ipc://" + endpoint, instanceId, startup.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(backoff, startup.Token); backoff = Math.Min(backoff * 2, 500);
                }
                var client = registry.Client(instanceId);
                var version = (await client.GetVersionAsync(deadline.Token)).Version;
                string config = Directory.CreateDirectory(Path.Combine(directory, "config/kicad", $"{version.Major}.{version.Minor}")).FullName;
                await File.WriteAllTextAsync(Path.Combine(config, "user.hotkeys"),
                    "eeschema.InspectionTool.runERC\tCtrl+F11\t\n", deadline.Token);
                var opened = await client.OpenRootSchematicAsync(schematic, deadline.Token);
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = opened.Document }, deadline.Token);
                var baseline = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = opened.Document }, deadline.Token);
                identities.Add(new(instanceId, client, baseline, process.Id, opened.Document, project));
                NativeKeyboard.SchematicShortcut(display, process.Id, "F11");
                using var visible = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                visible.CancelAfter(TimeSpan.FromSeconds(10));
                backoff = 25;
                while (!NativeKeyboard.HasWindow(display, process.Id, "Electrical Rules Checker"))
                { await Task.Delay(backoff, visible.Token); backoff = Math.Min(backoff * 2, 500); }
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{index}-erc-open.png"), deadline.Token);
                // Opening the native checker focuses its real Run ERC button.
                NativeKeyboard.SchematicShortcut(display, process.Id, "Return", "Electrical Rules Checker", false, false);
                var after = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = opened.Document }, deadline.Token);
                Assert.AreEqual(baseline.DocumentEpoch, after.DocumentEpoch);
                Assert.AreEqual(baseline.Sequence, after.Sequence, "Running checks is not an override edit.");
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{index}-erc-run.png"), deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(evidence, $"{index}-observation.json"), JsonSerializer.Serialize(new
                {
                    instanceId, processId = process.Id, project, document = opened.Document.ToString(),
                    baseline.DocumentEpoch, baseline.Sequence, dialogObserved = true,
                    overrideEditingQualified = false, trackingComplete = false
                }), deadline.Token);
            }
            Assert.AreNotEqual(identities[0].Baseline.DocumentEpoch, identities[1].Baseline.DocumentEpoch);
            Assert.AreEqual(2, registry.List().Count);
            foreach (var item in identities)
                Assert.AreEqual(item.Id, (await item.Client.HandshakeAsync(deadline.Token)).InstanceId);

            var first = identities[0];
            var other = identities[1];
            var originalTitle = await first.Client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = first.Document }, deadline.Token);
            byte[] otherProject = await File.ReadAllBytesAsync(other.Project, deadline.Token);
            var otherSaveState = await other.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = other.Document }, deadline.Token);

            // These positions are grounded in the retained 1280x900 GTK dialog
            // probe. Use its visible All filter, first violation and real menu.
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 75, clickFromBottom: 72);
            var computedErc = await CapturedErc(0, "");
            await Markers(markers => markers.Count == 4);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 250, clickFromTop: Row(0));
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "selected-violation.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "right-click", "Electrical Rules Checker", false,
                clickFromLeft: 250, clickFromTop: 85);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "exclusion-menu.png"), deadline.Token);
            foreach (string key in new[] { "Home", "Return" })
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, key, "Electrical Rules Checker", false, false);
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            changed.CancelAfter(TimeSpan.FromSeconds(10));
            SchematicChangeJournal journal;
            int changeBackoff = 25;
            while (true)
            {
                journal = await first.Client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = first.Document, DocumentEpoch = first.Baseline.DocumentEpoch,
                        AfterSequence = first.Baseline.Sequence }, changed.Token);
                if (journal.Sequence != first.Baseline.Sequence) break;
                await Task.Delay(changeBackoff, changed.Token); changeBackoff = Math.Min(changeBackoff * 2, 500);
            }
            Assert.AreEqual(first.Baseline.Sequence + 1, journal.Sequence);
            Assert.AreEqual("Edit ERC overrides", journal.Changes.Single().Description);
            var excludedSnapshot = await CapturedErc(1, "");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "excluded-violation.png"), deadline.Token);
            // Excluding is one undoable change. Undo and redo rebuild the list, which orders the
            // two errors, then the two warnings, then the exclusion.
            await History("exclude-undo", "z", computedErc, 0, "");
            await History("exclude-redo", "y", excludedSnapshot, 1, "");
            const int ExcludedRow = 3;
            var stale = new ApplySchematicItemBatch
            {
                Document = first.Document, DocumentEpoch = first.Baseline.DocumentEpoch,
                OperationId = Guid.NewGuid().ToString("D"),
                ExpectedRevision = new() { Epoch = first.Baseline.DocumentEpoch, Sequence = first.Baseline.Sequence }
            };
            stale.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = "must not overwrite user intent" } });
            var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                first.Client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, deadline.Token));
            StringAssert.Contains(rejected.Message, "Stale document revision");
            Assert.AreEqual(originalTitle, await first.Client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = first.Document }, deadline.Token));
            // The unsaved exclusion is live on its marker while the stored exclusion list that
            // saving rewrites still lags it. The native state digest must already describe what
            // saving writes: the save leaves it unchanged and only clears the unsaved flag.
            var excludedState = await Lifecycle();
            Assert.IsTrue(excludedState.NativeContentDirty, "The rendered exclusion is an unsaved edit.");
            await first.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = first.Document }, deadline.Token);
            var excludedSaved = await Lifecycle();
            Assert.AreEqual(excludedState.Revision, excludedSaved.Revision, "Saving is not an edit.");
            Assert.AreEqual(excludedState.StateSha256, excludedSaved.StateSha256,
                "The digest of an unsaved exclusion must equal the digest of the project saving writes.");
            Assert.IsFalse(excludedSaved.NativeContentDirty);
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token));
            Assert.AreEqual(1, saved.RootElement.GetProperty("erc").GetProperty("erc_exclusions").GetArrayLength());

            await Comment("cancel-comment", "cancelled", accept: false, changedValue: false, expected: "");
            await Comment("change-comment", "reason", accept: true, changedValue: true, expected: "reason");
            var reasonErc = await CapturedErc(1, "reason");
            await History("comment-undo", "z", excludedSnapshot, 1, "");
            await History("comment-redo", "y", reasonErc, 1, "reason");
            await Comment("unchanged-comment", "reason", accept: true, changedValue: false, expected: "reason");

            // The excluded row's first action restores this exact violation.
            var beforeRestore = await Journal();
            await Menu("restore-menu", 0, ExcludedRow);
            var restored = await Changed(beforeRestore);
            var restoredErc = await CapturedErc(0, "");
            await first.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = first.Document }, deadline.Token);
            using (var restoredFile = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token)))
                Assert.AreEqual(0, restoredFile.RootElement.GetProperty("erc").GetProperty("erc_exclusions").GetArrayLength());
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "restored-violation.png"), deadline.Token);
            await History("restore-undo", "z", reasonErc, 1, "reason");
            // Redo lists the restored violation first again, before the other error.
            await History("restore-redo", "y", restoredErc, 0, "");

            // The retained real menu has Exclude, Exclude with comment, then
            // Change severity. GTK navigation skips the separator.
            var beforeSeverity = await Journal();
            await Menu("severity-menu", 2, 0);
            var severity = await Changed(beforeSeverity);
            var capturedSeverity = await CapturedErc(0, "");
            Assert.AreEqual(RuleSeverity.RsWarning,
                capturedSeverity.RuleSeverities.Single(rule => (int)rule.RuleType == 3).Severity);
            await first.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = first.Document }, deadline.Token);
            using (var severityFile = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token)))
            {
                Assert.AreEqual("warning", severityFile.RootElement.GetProperty("erc")
                    .GetProperty("rule_severities").GetProperty("pin_not_connected").GetString());
                Assert.AreEqual(0, severityFile.RootElement.GetProperty("erc").GetProperty("erc_exclusions").GetArrayLength());
            }
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "changed-severity.png"), deadline.Token);
            await History("severity-undo", "z", restoredErc, 0, "");
            await History("severity-redo", "y", capturedSeverity, 0, "");

            // Ignoring the rule is one undoable change that also deletes its two computed
            // violations. Undo restores the saved severity, not computed violations.
            var beforeIgnore = await Journal();
            await Menu("ignore-menu", 3, 0);
            await Changed(beforeIgnore);
            var ignoredErc = await CapturedErc(0, "");
            Assert.AreEqual(RuleSeverity.RsIgnore, ignoredErc.RuleSeverities.Single(rule => (int)rule.RuleType == 3).Severity);
            var library = await Markers(markers => markers.Count == 2);
            Assert.IsFalse(library.Any(marker => (int)marker.Marker.ErrorType == 3), "Ignoring a rule deletes its violations.");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "ignored-rule.png"), deadline.Token);
            await History("ignore-undo", "z", capturedSeverity, 0, "");
            Assert.AreEqual(2, (await Markers(_ => true)).Count, "Undo restores the saved severity, never computed violations.");
            await History("ignore-redo", "y", ignoredErc, 0, "");

            // Deleting a computed violation changes nothing that is saved: no revision, no
            // unsaved flag and the same state digest. The next Run ERC computes it again.
            var beforeComputed = await Lifecycle();
            var beforeComputedJournal = await Journal();
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 250, clickFromTop: Row(0));
            var computed = library.Select(marker => marker.Marker).ToHashSet();
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 78, clickFromBottom: 25);
            var remaining = await Markers(markers => markers.Count == 1);
            var deletedComputed = computed.Single(marker => !remaining.Any(live => live.Marker.Equals(marker)));
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "deleted-computed-violation.png"), deadline.Token);
            await Unrecorded(beforeComputedJournal, beforeComputed, "Deleting a computed violation");
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromRight: 58, clickFromBottom: 25);
            var rerun = await Markers(markers => markers.Count == 2);
            Assert.IsTrue(rerun.Any(marker => marker.Marker.Equals(deletedComputed) && !marker.Excluded),
                "Run ERC computes the deleted violation again.");
            await Unrecorded(beforeComputedJournal, beforeComputed, "Running the checks");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "rerun-computed-violation.png"), deadline.Token);

            // With no remaining exclusions, this real action removes only
            // computed markers and records nothing. The XML restore must recreate its exclusion.
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 230, clickFromBottom: 25);
            await Markers(markers => markers.Count == 0);
            await Unrecorded(beforeComputedJournal, beforeComputed, "Deleting only computed violations");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "before-xml-restore.png"), deadline.Token);
            var beforeXml = await CapturedErc(0, "");
            var desired = excludedSnapshot.Clone();
            desired.Exclusions[0].Comment = "restored";
            desired.PinMap[0].Conflict = (Kiapi.Schematic.Types.SchematicErcPinConflict)
                ((int)desired.PinMap[0].Conflict == 2 ? 3 : 2);
            var fromXml = (Kiapi.Schematic.Types.SchematicErcSettings)SchematicDataXml.Read(SchematicDataXml.Write(desired));
            var beforeXmlJournal = await Journal();
            var replace = new ApplySchematicItemBatch
            {
                Document = first.Document, DocumentEpoch = beforeXmlJournal.DocumentEpoch,
                ExpectedRevision = new() { Epoch = beforeXmlJournal.DocumentEpoch, Sequence = beforeXmlJournal.Sequence },
                OperationId = Guid.NewGuid().ToString("D"), Description = "Restore ERC from XML"
            };
            var nativeScreen = await first.Client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = first.Document }, deadline.Token);
            var desiredScreen = nativeScreen.Data.Clone(); desiredScreen.Metadata.ErcSettings = fromXml;
            var planned = SchematicItemDelta.Plan(nativeScreen.Data, desiredScreen);
            Assert.AreEqual(1, planned.Count);
            Assert.IsNotNull(planned[0].SetErcSettings);
            replace.Operations.Add(planned);
            var failing = replace.Clone(); failing.OperationId = Guid.NewGuid().ToString("D");
            failing.Operations.Add(new SchematicItemOperation());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                first.Client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failing, deadline.Token));
            Assert.AreEqual(beforeXml, await CapturedErc(0, ""));
            Assert.AreEqual(beforeXmlJournal.Sequence, (await Journal()).Sequence);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "xml-rollback.png"), deadline.Token);

            var applied = await first.Client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(replace, deadline.Token);
            Assert.IsTrue(applied.ErcSettingsChanged);
            Assert.AreEqual(desired, await CapturedErc(1, "restored"));
            Assert.AreEqual(applied, await first.Client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(replace, deadline.Token),
                "Identical retries must reuse the original native receipt.");
            var noOp = replace.Clone(); noOp.OperationId = Guid.NewGuid().ToString("D");
            noOp.ExpectedRevision = applied.Revision.Clone();
            var reversed = desired.Clone();
            var rulesReversed = reversed.RuleSeverities.Reverse().ToArray();
            var pinsReversed = reversed.PinMap.Reverse().ToArray();
            reversed.RuleSeverities.Clear(); reversed.RuleSeverities.Add(rulesReversed);
            reversed.PinMap.Clear(); reversed.PinMap.Add(pinsReversed);
            noOp.Operations[0].SetErcSettings = reversed;
            var unchanged = await first.Client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(noOp, deadline.Token);
            Assert.IsFalse(unchanged.ErcSettingsChanged);
            Assert.AreEqual(applied.Revision, unchanged.Revision);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "xml-restored.png"), deadline.Token);

            await History("xml-z", "z", beforeXml, 0, "");
            Assert.AreEqual(0, (await Markers(_ => true)).Count, "Undo deletes the marker the XML restore created.");
            await History("xml-y", "y", desired, 1, "restored");
            await first.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = first.Document }, deadline.Token);
            using (var restoredProject = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token)))
                Assert.AreEqual("restored", restoredProject.RootElement.GetProperty("erc")
                    .GetProperty("erc_exclusions")[0].GetProperty("comment").GetString());

            // Redo restored the exclusion's marker, and the open dialog lists it again. Deleting
            // that excluded violation deletes a saved exclusion: exactly one tracked revision and
            // an unsaved edit, while the saved project keeps the exclusion until the next save.
            var beforeDelete = await Journal();
            var cleanBeforeDelete = await Lifecycle();
            Assert.IsFalse(cleanBeforeDelete.NativeContentDirty);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 250, clickFromTop: 85);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "delete-exclusion-selected.png"), deadline.Token);
            // The retained 1280x900 dialog places Delete Marker left of Delete All Markers.
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 78, clickFromBottom: 25);
            await Changed(beforeDelete, "Delete ERC exclusion");
            var deletedErc = await CapturedErc(0, "");
            var deletedState = await Lifecycle();
            Assert.AreEqual(beforeDelete.Sequence + 1, deletedState.Revision.Sequence);
            Assert.IsTrue(deletedState.NativeContentDirty, "Deleting a saved exclusion is an unsaved edit.");
            Assert.AreNotEqual(cleanBeforeDelete.StateSha256, deletedState.StateSha256,
                "Deleting a saved exclusion must change the native state digest.");
            using (var unsaved = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token)))
                Assert.AreEqual("restored", unsaved.RootElement.GetProperty("erc")
                    .GetProperty("erc_exclusions")[0].GetProperty("comment").GetString());
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "deleted-exclusion.png"), deadline.Token);
            // Undo brings the saved exclusion back exactly: the digest equals the saved state's.
            await History("delete-exclusion-undo", "z", desired, 1, "restored");
            Assert.AreEqual(cleanBeforeDelete.StateSha256, (await Lifecycle()).StateSha256,
                "Undoing the deletion restores exactly the saved exclusion.");
            await History("delete-exclusion-redo", "y", deletedErc, 0, "");
            Assert.AreEqual(deletedState.StateSha256, (await Lifecycle()).StateSha256);
            await History("delete-exclusion-undo-again", "z", desired, 1, "restored");

            // Run ERC resolves the saved exclusion onto its computed violation and records nothing.
            var beforeRun = await Lifecycle();
            var beforeRunJournal = await Journal();
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromRight: 58, clickFromBottom: 25);
            var checkedMarkers = await Markers(markers => markers.Count == 4);
            Assert.AreEqual(desired.Exclusions[0].Marker, checkedMarkers.Single(marker => marker.Excluded).Marker,
                "Run ERC keeps the exclusion on the violation with its exact references.");
            await Unrecorded(beforeRunJournal, beforeRun, "Running the checks with an exclusion");
            Assert.AreEqual(desired, await CapturedErc(1, "restored"));

            // Delete All Markers with exclusions is one undoable change that removes every marker
            // from the schematic, the exclusion's too.
            var beforeDeleteAll = await Journal();
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 230, clickFromBottom: 25);
            using (var prompt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                prompt.CancelAfter(TimeSpan.FromSeconds(10));
                int delay = 25;
                while (!NativeKeyboard.HasWindow(display, first.ProcessId, "Delete All Markers"))
                { await Task.Delay(delay, prompt.Token); delay = Math.Min(delay * 2, 500); }
            }
            var promptWindows = new List<string>();
            NativeKeyboard.HasWindow(display, first.ProcessId, "Delete All Markers", describe: promptWindows.Add);
            await File.WriteAllLinesAsync(Path.Combine(evidence, "delete-all-prompt-windows.txt"), promptWindows, deadline.Token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "delete-all-prompt.png"), deadline.Token);
            // The GTK alert orders its buttons [No] [Cancel] [Yes] and focuses Yes ("Errors and
            // Warnings Only"); No is "Errors, Warnings and Exclusions".
            foreach (string key in new[] { "Left", "Left" })
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, key, "Delete All Markers", false, false);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "delete-all-choice.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "Return", "Delete All Markers", false, false);
            await Changed(beforeDeleteAll, "Delete ERC exclusions");
            Assert.AreEqual(deletedErc, await CapturedErc(0, ""));
            await Markers(markers => markers.Count == 0);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "motion", "Electrical Rules Checker", false, false);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "deleted-all-with-exclusions.png"), deadline.Token);
            await History("delete-all-undo", "z", desired, 1, "restored");
            Assert.AreEqual(desired.Exclusions[0].Marker, (await Markers(markers => markers.Count == 1)).Single().Marker,
                "Undo rebuilds the excluded violation's marker, not the computed ones.");
            await History("delete-all-redo", "y", deletedErc, 0, "");
            await Markers(markers => markers.Count == 0);

            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromRight: 150, clickFromBottom: 25);
            using (var closed = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                closed.CancelAfter(TimeSpan.FromSeconds(10));
                while (NativeKeyboard.HasWindow(display, first.ProcessId, "Electrical Rules Checker"))
                    await Task.Delay(50, closed.Token);
            }
            // Revert discards the unsaved deletion. This process keeps its project settings loaded,
            // so the saved exclusion returns from the stored exclusion list: every read-only capture
            // since the deletion must have left that list exactly as the last save wrote it.
            await first.Client.InvokeAsync<RevertDocument, Empty>(new() { Document = first.Document }, deadline.Token);
            var reopened = await first.Client.OpenRootSchematicAsync(Path.ChangeExtension(first.Project, ".kicad_sch"), deadline.Token);
            Assert.AreEqual(first.Document, reopened.Document);
            Assert.IsTrue(SchematicErcSettingsValidation.Same(desired, await CapturedErc(1, "restored")));

            var otherJournal = await other.Client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                new() { Document = other.Document }, deadline.Token);
            Assert.AreEqual(other.Baseline.Sequence, otherJournal.Sequence);
            Assert.AreEqual(otherSaveState, await other.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = other.Document }, deadline.Token));
            CollectionAssert.AreEqual(otherProject, await File.ReadAllBytesAsync(other.Project, deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(evidence, "exclusion-result.json"), JsonSerializer.Serialize(new
            {
                instanceId = first.Id, otherInstanceId = other.Id, documentEpoch = journal.DocumentEpoch,
                revision = (await Journal()).Sequence, renderedExclusionCommitted = true, staleEditRejected = true,
                commentCancellationPreserved = true, commentChangePersisted = true, unchangedCommentPreserved = true,
                exclusionRestored = true, severityChangePersisted = true, otherProjectPreserved = true,
                liveErcXmlCaptureVerified = true,
                xmlRestoreVerified = true, failedBatchRolledBack = true, retryReusedReceipt = true,
                reorderedPolicyNoOp = true, nativeUndoRedoVerified = true,
                plannedXmlDeltaApplied = true, nativeSaveReopenVerified = true,
                unsavedExclusionDigestMatchesSave = true, renderedExclusionDeletionTracked = true,
                revertRestoredSavedExclusion = true, exclusionUndoRedo = true, commentUndoRedo = true,
                restoreUndoRedo = true, severityUndoRedo = true, ignoreRuleUndoRedo = true,
                exclusionDeletionUndoable = true, computedDeletionUnrecorded = true, runErcRecomputedViolation = true,
                deleteAllWithExclusionsUndoable = true, completeErcOverrideCoverage = false, trackingComplete = false
            }), deadline.Token);

            Task<SchematicChangeJournal> Journal() => first.Client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                new() { Document = first.Document }, deadline.Token);

            Task<DocumentLifecycleState> Lifecycle() => first.Client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = first.Document }, deadline.Token);

            // The list rows of the retained 1280x900 dialog: one violation and its item per 46 px.
            static int Row(int index) => 85 + 46 * index;

            // The live markers on every screen, once the rendered action has produced them.
            async Task<IReadOnlyList<SchematicTrackingErcMarker>> Markers(Func<IReadOnlyList<SchematicTrackingErcMarker>, bool> accept)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(10));
                int delay = 25;
                while (true)
                {
                    try
                    {
                        var markers = (await first.Client.InvokeAsync<SchematicTrackingReadErcMarkers, SchematicTrackingErcMarkers>(
                            new() { Document = first.Document }, wait.Token)).Markers;
                        if (accept(markers)) return markers;
                    }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
                }
            }

            // Nothing saved changed: the same revision, unsaved flag and state digest.
            async Task Unrecorded(SchematicChangeJournal before, DocumentLifecycleState state, string what)
            {
                var after = await Journal();
                Assert.AreEqual(before.DocumentEpoch, after.DocumentEpoch, what + " starts no new document session.");
                Assert.AreEqual(before.Sequence, after.Sequence, what + " records no revision.");
                var lifecycle = await Lifecycle();
                Assert.AreEqual(state.Revision, lifecycle.Revision);
                Assert.AreEqual(state.NativeContentDirty, lifecycle.NativeContentDirty, what + " leaves the unsaved flag alone.");
                Assert.AreEqual(state.StateSha256, lifecycle.StateSha256, what + " leaves the saved state unchanged.");
            }

            // Keyboard undo or redo is exactly one journal entry and restores exactly the expected settings.
            async Task History(string stage, string key, Kiapi.Schematic.Types.SchematicErcSettings expected, int exclusions,
                string comment)
            {
                var before = await Journal();
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, key);
                await Changed(before, key == "z" ? "Undo" : "Redo");
                Assert.AreEqual(expected, await CapturedErc(exclusions, comment), stage + " must restore the exact ERC settings.");
                Assert.IsFalse(Process.GetProcessById(first.ProcessId).HasExited);
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, "motion", "Electrical Rules Checker", false, false);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, stage + ".png"), deadline.Token);
            }

            async Task<Kiapi.Schematic.Types.SchematicErcSettings> CapturedErc(int exclusions, string comment)
            {
                byte[] savedBefore = await File.ReadAllBytesAsync(first.Project, deadline.Token);
                var before = await Journal();
                var metadata = await first.Client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
                    new() { Document = first.Document }, deadline.Token);
                var erc = metadata.Metadata.ErcSettings;
                Assert.IsNotNull(erc, "The actual native peer must capture typed ERC settings.");
                SchematicErcSettingsValidation.Validate(erc, erc.RuleSeverities.Select(rule => rule.RuleType).ToHashSet());
                Assert.AreEqual(erc, SchematicDataXml.Read(SchematicDataXml.Write(erc)));
                Assert.AreEqual(exclusions, erc.Exclusions.Count);
                if (exclusions == 1) Assert.AreEqual(comment, erc.Exclusions[0].Comment);
                Assert.AreEqual(before.Sequence, (await Journal()).Sequence);
                CollectionAssert.AreEqual(savedBefore, await File.ReadAllBytesAsync(first.Project, deadline.Token),
                    "Read-only capture must not synchronize the saved exclusion cache behind the editor.");
                return erc;
            }

            async Task<SchematicChangeJournal> Changed(SchematicChangeJournal before, string description = "Edit ERC overrides")
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(10));
                int delay = 25;
                while (true)
                {
                    var after = await first.Client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                        new() { Document = first.Document, DocumentEpoch = before.DocumentEpoch,
                            AfterSequence = before.Sequence }, wait.Token);
                    if (after.Sequence != before.Sequence)
                    {
                        Assert.AreEqual(before.Sequence + 1, after.Sequence);
                        Assert.AreEqual(description, after.Changes.Single().Description);
                        return after;
                    }
                    await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
                }
            }

            async Task Menu(string stage, int item, int row)
            {
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, "right-click", "Electrical Rules Checker", false,
                    clickFromLeft: 250, clickFromTop: Row(row));
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, stage + ".png"), deadline.Token);
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, "Home", "Electrical Rules Checker", false, false);
                for (int index = 0; index < item; ++index)
                    NativeKeyboard.SchematicShortcut(display, first.ProcessId, "Down", "Electrical Rules Checker", false, false);
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, "Return", "Electrical Rules Checker", false, false);
            }

            async Task Comment(string stage, string text, bool accept, bool changedValue, string expected)
            {
                var before = await Journal();
                var beforeSave = await first.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = first.Document }, deadline.Token);
                byte[] beforeFile = await File.ReadAllBytesAsync(first.Project, deadline.Token);
                await Menu(stage + "-menu", 1, ExcludedRow);
                using var window = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                window.CancelAfter(TimeSpan.FromSeconds(10));
                int delay = 25;
                while (!NativeKeyboard.HasWindow(display, first.ProcessId, "Exclusion Comment"))
                { await Task.Delay(delay, window.Token); delay = Math.Min(delay * 2, 500); }
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, "a", "Exclusion Comment", true, false);
                foreach (char letter in text)
                    NativeKeyboard.SchematicShortcut(display, first.ProcessId, letter.ToString(), "Exclusion Comment", false, false);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, stage + ".png"), deadline.Token);
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Exclusion Comment", false,
                    clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
                delay = 25;
                while (NativeKeyboard.HasWindow(display, first.ProcessId, "Exclusion Comment"))
                { await Task.Delay(delay, window.Token); delay = Math.Min(delay * 2, 500); }
                if (changedValue)
                {
                    await Changed(before);
                    await CapturedErc(1, expected);
                    await first.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = first.Document }, deadline.Token);
                }
                else
                {
                    var after = await Journal();
                    Assert.AreEqual(before.DocumentEpoch, after.DocumentEpoch);
                    Assert.AreEqual(before.Sequence, after.Sequence);
                    Assert.AreEqual(beforeSave, await first.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                        new() { Document = first.Document }, deadline.Token));
                    CollectionAssert.AreEqual(beforeFile, await File.ReadAllBytesAsync(first.Project, deadline.Token));
                }
                using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token));
                var exclusions = persisted.RootElement.GetProperty("erc").GetProperty("erc_exclusions");
                Assert.AreEqual(1, exclusions.GetArrayLength());
                Assert.AreEqual(expected, exclusions[0].TryGetProperty("comment", out var comment) ? comment.GetString() : "");
            }
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString());
            if (display is not null)
            {
                using var captureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "failure-screen.png"), captureDeadline.Token); }
                catch (Exception captureError)
                { await File.WriteAllTextAsync(Path.Combine(evidence, "capture-failure.txt"), captureError.ToString()); }
            }
            throw;
        }
        finally
        {
            // Every process and file belongs to this disposable fixture; no
            // installed editor or user document is closed by cleanup.
            foreach (var process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(); process.Dispose();
            }
            await Task.WhenAll(logs);
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed record ErcDesign(string Id, NativeClient Client, SchematicChangeJournal Baseline,
        int ProcessId, DocumentSpecifier Document, string Project);

    private static async Task Capture(StreamReader reader, string path)
    {
        await using var output = new StreamWriter(path);
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            await output.WriteAsync(buffer.AsMemory(0, count));
    }
}
