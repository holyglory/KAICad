using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ErcExclusion = Kiapi.Schematic.ErcExclusion;
using ErcMarker = Kiapi.Schematic.ErcMarker;
using ErcRule = Kiapi.Schematic.ErcErrorType;
using ErcSettings = Kiapi.Schematic.Types.SchematicErcSettings;
using PinConflict = Kiapi.Schematic.Types.SchematicErcPinConflict;
using SymbolInstance = Kiapi.Schematic.Types.SchematicSymbolInstance;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyAutomaticSynchronization(NativeClient client, DocumentSpecifier root,
        DesignRecoveryStore store, string designPath, string evidence, string instanceId, int processId, string display,
        CancellationToken token)
    {
        // The original journey used 60 s. The ERC round trips add one rendered Setup
        // edit, nine keyboard undo/redo steps, one XML apply, one rendered ERC dialog
        // deletion and a save/reload reattachment, each a full worker synchronization with
        // a checked native save, and a fresh KiCad process that reopens the saved files
        // (bounded at 60 s). Each synchronization has its own 90 s failure ceiling.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(360));
        var driver = await AutomaticDesignDriver.CreateAsync(store, client, designPath, store.Read()!.RevisionToken, deadline.Token);
        await using var session = new AutomaticDesignSynchronization(driver);
        await Watching(_ => true);
        var conflict = await Assert.ThrowsExactlyAsync<KiCad.Automation.Model.AutomationException>(() =>
            AutomaticDesignDriver.CreateAsync(store, client, designPath, store.Read()!.RevisionToken, deadline.Token));
        Assert.AreEqual("automatic_sync_ownership_conflict", conflict.Code);

        string title = "Automatic native commit " + instanceId;
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = root.Clone(), TitleBlock = new() { Title = title } }, deadline.Token);
        await Watching(design => design.Schematic.Instances.Single(s => s.Metadata.Document.Equals(root)).Metadata.TitleBlock.Title == title);
        var afterNative = await Capture(); var parsed = Read();
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(afterNative.Electrical.Hierarchy.Data, parsed.Schematic, deadline.Token));

        var target = parsed.Engineering.Circuit.Components.First(c => parsed.Engineering.Circuit.Symbols.Any(s => s.ComponentId == c.Id));
        string nextReference = "AUTO900";
        var desired = parsed with { Engineering = parsed.Engineering with { Circuit = parsed.Engineering.Circuit with
        { Components = parsed.Engineering.Circuit.Components.Select(c => c.Id == target.Id ? c with { Reference = nextReference } : c).ToArray() } } };
        await File.WriteAllTextAsync(designPath, SchematicDesignXml.Write(desired, []), Encoding.UTF8, deadline.Token);
        await Watching(design => design.Engineering.Circuit.Components.Single(c => c.Id == target.Id).Reference == nextReference
            && SchematicDesignBindings.Inspect(design, []).Differences.Count == 0);
        var afterXml = await Capture(); parsed = Read();
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(afterXml.Electrical.Hierarchy.Data, parsed.Schematic, deadline.Token));
        Assert.IsTrue(SchematicElectricalComparison.Compare(parsed, afterXml.Electrical, [], deadline.Token).ConnectivityEquivalent);

        byte[] valid = await File.ReadAllBytesAsync(designPath, deadline.Token);
        await File.WriteAllBytesAsync(designPath, [0xff], deadline.Token);
        await Status(s => s.Phase == AutomaticDesignPhase.InvalidDesign);
        CollectionAssert.AreEqual(new byte[] { 0xff }, store.Read()!.State.DesiredFileBytes);
        Assert.AreEqual(afterXml, await Capture(), "Invalid XML cannot change native objects or revision.");
        await File.WriteAllBytesAsync(designPath, valid, deadline.Token);
        await Watching(_ => true);
        var savedErc = await VerifyAutomaticErcSynchronization(session, client, root, store, designPath, processId, display,
            evidence, instanceId, deadline.Token);
        var settled = store.Read()!; byte[] settledFile = await File.ReadAllBytesAsync(designPath, deadline.Token);
        var settledNative = await Capture();
        // An identical saved file can notify the watcher but must not generate
        // another native edit, baseline publication or operation receipt.
        await File.WriteAllBytesAsync(designPath, settledFile, deadline.Token);
        await session.DisposeAsync();
        Assert.AreEqual(AutomaticDesignPhase.Stopped, session.Inspect().Phase);
        Assert.AreEqual(settledNative.State.ProcessEpoch, (await Capture()).State.ProcessEpoch);
        Assert.AreEqual(settledNative.State.Revision, (await Capture()).State.Revision);
        Assert.AreEqual(settled.State.LastSynchronization?.OperationId, store.Read()!.State.LastSynchronization?.OperationId);

        // A native edit made while stopped is recovered on a new subscription.
        string missed = "Automatic reattach " + instanceId;
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = root.Clone(), TitleBlock = new() { Title = missed } }, deadline.Token);
        var newDriver = await AutomaticDesignDriver.CreateAsync(store, client, designPath, store.Read()!.RevisionToken, deadline.Token);
        var reattached = new AutomaticDesignSynchronization(newDriver);
        await Wait(reattached, s => s.Phase == AutomaticDesignPhase.Watching
            && Read().Schematic.Instances.Single(item => item.Metadata.Document.Equals(root)).Metadata.TitleBlock.Title == missed);
        Assert.IsTrue(SchematicElectricalComparison.Compare(Read(), (await Capture()).Electrical, [], deadline.Token).ConnectivityEquivalent);
        await reattached.DisposeAsync();
        await using var publicHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-automatic-public-host"), Path.Combine(evidence, instanceId + "-automatic-public-host.log"), deadline.Token);
        RequireToolSuccess(await publicHost.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var current = store.Read()!;
        var publicStart = await publicHost.Tool("kicad_design_automatic_sync_start", new
        { instanceId, recoveryPath = store.StatePath, designPath, expectedRecoveryRevision = current.RevisionToken });
        RequireToolSuccess(publicStart);
        string publicSession = publicStart.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
        var listed = await publicHost.Tool("kicad_design_automatic_sync_list", new { instanceId });
        RequireToolSuccess(listed); Assert.HasCount(1, listed.GetProperty("structuredContent").GetProperty("sessions").EnumerateArray());
        var waited = await publicHost.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId = publicSession, afterSequence = 0UL });
        RequireToolSuccess(waited);
        var publicStop = await publicHost.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId = publicSession });
        RequireToolSuccess(publicStop);
        Assert.AreEqual("Stopped", publicStop.GetProperty("structuredContent").GetProperty("status").GetProperty("phase").GetString());
        await VerifyAutomaticErcReload(publicHost, client, root, store, designPath, savedErc, display, evidence, instanceId,
            deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-sync-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeCommitPublishedWithoutApplyCall = true, xmlSaveAppliedWithoutApplyCall = true,
            invalidXmlPreserved = true, correctedXmlResumed = true, competingOwnerRejected = true,
            stopPreservedEditor = true, missedEditRecoveredAfterReattach = true,
            publicToolsQualified = true, ercNativeSetupEditPublished = true, ercXmlEditApplied = true,
            ercNativeUndoRedoPublished = true, ercDialogRunErcUndoRedoSafe = true, ercDialogDeleteMarkerUndoable = true,
            ercSavedProjectComplete = true, ercReopenedFromDiskInFreshProcess = true,
            ercRevertedAndReattached = true, crossPlatformReady = false
        }), deadline.Token);

        SchematicDesign Read() => SchematicDesignXml.Read(File.ReadAllText(designPath), []);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, deadline.Token);
        Task Watching(Func<SchematicDesign, bool> accept) => Wait(session, s => s.Phase == AutomaticDesignPhase.Watching && accept(Read()));
        Task Status(Func<AutomaticDesignStatus, bool> accept) => Wait(session, accept);
        async Task Wait(AutomaticDesignSynchronization current, Func<AutomaticDesignStatus, bool> accept)
        {
            var status = current.Inspect();
            try
            {
                while (!accept(status))
                {
                    Assert.IsFalse(status.Phase is AutomaticDesignPhase.Paused or AutomaticDesignPhase.Stopped,
                        status.ErrorCode + ": " + status.ErrorMessage);
                    status = await current.WaitAsync(status.Sequence, deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-timeout.json"),
                    JsonSerializer.Serialize(new { status = current.Inspect(), recovery = store.Read() }), CancellationToken.None);
                throw;
            }
        }
    }

    // Project ERC policy, the pin conflict matrix and exclusions travel through the
    // running automatic worker in both directions and through native undo/redo, with
    // nothing else in the design changing. Returns the settings KiCad must reload.
    private static async Task<ErcSettings> VerifyAutomaticErcSynchronization(AutomaticDesignSynchronization session,
        NativeClient client, DocumentSpecifier root, DesignRecoveryStore store, string designPath, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        var original = await Capture(); var originalDesign = Read();
        var originalErc = Erc(original.Electrical.Hierarchy.Data);
        Assert.IsTrue(SchematicErcSettingsValidation.Same(originalErc, Erc(originalDesign.Schematic)),
            "The settled XML must already carry the live native ERC settings.");

        // Native to XML: a rendered Schematic Setup edit of the pin conflict matrix.
        // Nothing calls apply; the worker observes the native commit and publishes it.
        var setupErc = originalErc.Clone();
        var inputPair = setupErc.PinMap.Single(cell => cell.First == ElectricalPinType.EptInput && cell.Second == ElectricalPinType.EptInput);
        inputPair.Conflict = (PinConflict)((int)inputPair.Conflict % 3 + 1);
        ulong cursor = session.Inspect().Sequence;
        await NativeSetupUi.Open(client, root, display, processId, token);
        // The same rendered page row and measured input/input matrix cell that
        // VerifySetupPinMap uses on this fixed 1280x900 Linux display.
        await NativeSetupUi.SelectPage(display, processId, 180, token);
        // As VerifySetupPinMap does, retain the selected page before the click: the
        // matrix page is built lazily, and a click before it is shown edits nothing.
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-setup-selected.png"), token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false, clickFromLeft: 414, clickFromTop: 59);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-setup-edited.png"), token);
        Assert.IsFalse(NativeKeyboard.HasWindow(display, processId, "Import Settings"),
            "The matrix click must edit a pin rule, not open the global Import action.");
        // Every accepted Setup journey returns to the Formatting row before OK.
        await NativeSetupUi.SelectPage(display, processId, 34, token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false, clickFromRight: 60, clickFromBottom: 25);
        using (var closing = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            closing.CancelAfter(TimeSpan.FromSeconds(10));
            int delay = 25;
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                { await Task.Delay(delay, closing.Token); delay = Math.Min(delay * 2, 500); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                var windows = new List<string>();
                NativeKeyboard.HasWindow(display, processId, "Schematic Setup", describe: windows.Add);
                await File.WriteAllLinesAsync(Path.Combine(evidence, instanceId + "-automatic-erc-setup-windows.txt"), windows, token);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-setup-close-failed.png"), token);
                // A Debug build reports a failed native assertion in a modal window
                // titled by the application. Retain its expanded native backtrace.
                if (windows.Any(window => window.Contains(": kicad, map=2,", StringComparison.Ordinal)))
                {
                    NativeKeyboard.SchematicShortcut(display, processId, "click", "kicad", false, clickFromLeft: 45, clickFromTop: 97);
                    await Task.Delay(500, token);
                    NativeKeyboard.SchematicShortcut(display, processId, "", "kicad", false, false, resizeWidth: 1280, resizeHeight: 900);
                    await Task.Delay(500, token);
                    await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-setup-assert.png"), token);
                    // The rendered backtrace omits hidden kiface frames; the Debug
                    // build's symbols identify them. Diagnostics only, never success.
                    var debug = new System.Diagnostics.ProcessStartInfo("gdb")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (string argument in new[] { "-p", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-batch", "-ex", "thread 1", "-ex", "bt 80" })
                        debug.ArgumentList.Add(argument);
                    try
                    {
                        using var debugger = System.Diagnostics.Process.Start(debug)!;
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        try
                        {
                            var output = debugger.StandardOutput.ReadToEndAsync(limit.Token);
                            var errors = debugger.StandardError.ReadToEndAsync(limit.Token);
                            await debugger.WaitForExitAsync(limit.Token);
                            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-setup-assert-backtrace.txt"),
                                await output + "\n--- stderr ---\n" + await errors, CancellationToken.None);
                        }
                        finally { if (!debugger.HasExited) debugger.Kill(entireProcessTree: true); }
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
                    {
                        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-setup-assert-backtrace.txt"),
                            "Backtrace unavailable: " + error, CancellationToken.None);
                    }
                }
                throw;
            }
        }
        // Accepting the edited Setup records one native change before the worker can see it.
        using (var recorded = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            recorded.CancelAfter(TimeSpan.FromSeconds(30));
            int delay = 25;
            try
            {
                while (true)
                {
                    var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                        new() { Document = root.Clone() }, recorded.Token);
                    Assert.AreEqual(original.State.Revision.Epoch, journal.DocumentEpoch);
                    if (journal.Sequence != original.State.Revision.Sequence) break;
                    await Task.Delay(delay, recorded.Token); delay = Math.Min(delay * 2, 500);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Assert.Fail("The rendered Setup edit recorded no native change; see the retained "
                    + instanceId + "-automatic-erc-setup-selected/edited screenshots.");
            }
        }
        var setupNative = await Synchronized("native-setup", cursor, setupErc);
        Assert.AreEqual(original.State.Revision.Epoch, setupNative.State.Revision.Epoch, "The Setup edit keeps the document session.");
        Assert.AreEqual(original.State.Revision.Sequence + 1, setupNative.State.Revision.Sequence,
            "Accepting the Setup edit must add exactly one revision.");
        var setupDesign = Read();
        OnlyErcChanged("The Setup edit", original.Electrical.Hierarchy.Data, setupNative.Electrical.Hierarchy.Data, setupErc);
        OnlyErcChanged("The published XML", originalDesign.Schematic, setupDesign.Schematic, setupErc);
        await SameOutsideSchematic("native-setup", originalDesign, setupDesign);
        var nativePublication = store.Read()!.State.LastSynchronization!;
        Assert.IsFalse(nativePublication.NativeMutationCommitted, "A native edit is published to XML, not replayed into KiCad.");
        Assert.IsTrue(nativePublication.NativeFilesSaved, "The worker saves the checked native files it published.");
        using (var project = JsonDocument.Parse(await File.ReadAllBytesAsync(ProjectFile(root), token)))
            Assert.AreEqual((int)inputPair.Conflict - 1, project.RootElement.GetProperty("erc").GetProperty("pin_map")[0][0].GetInt32());

        // Keyboard undo and redo of the Setup edit are native changes the worker publishes.
        var setupUndone = await History("setup-undo", "z", originalErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(original.Electrical.Hierarchy.Data, setupUndone.Electrical.Hierarchy.Data, token),
            "Undoing the Setup edit must restore the complete native design.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(originalDesign.Schematic, Read().Schematic, token),
            "The XML must return to the design it had before the Setup edit.");
        await SameOutsideSchematic("native-setup-undo", originalDesign, Read());
        var setupRedone = await History("setup-redo", "y", setupErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(setupNative.Electrical.Hierarchy.Data, setupRedone.Electrical.Hierarchy.Data, token),
            "Redoing the Setup edit must restore the complete edited native design.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(setupDesign.Schematic, Read().Schematic, token),
            "The XML must return to the design the Setup edit published.");
        await SameOutsideSchematic("native-setup-redo", originalDesign, Read());

        // XML to native: one saved XML edit changes a rule severity, another matrix
        // pair and adds an exclusion with exact marker references and a comment.
        var beforeXml = await Capture(); var xmlDesign = Read();
        var xmlErc = setupErc.Clone();
        var rule = xmlErc.RuleSeverities.Single(r => r.RuleType == ErcRule.ErcetPinNotConnected);
        rule.Severity = rule.Severity == RuleSeverity.RsWarning ? RuleSeverity.RsError : RuleSeverity.RsWarning;
        var outputPair = xmlErc.PinMap.Single(cell => cell.First == ElectricalPinType.EptOutput && cell.Second == ElectricalPinType.EptOutput);
        outputPair.Conflict = (PinConflict)((int)outputPair.Conflict % 3 + 1);
        var anchor = beforeXml.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .First(item => item.Is(SymbolInstance.Descriptor)).Unpack<SymbolInstance>();
        // SCH_SHEET_PATH::Path() omits the virtual root, so the marker's sheet
        // references are exactly the root document path, without display text.
        var rootPath = new SheetPath(); rootPath.Path.Add(root.SheetPath.Path.Select(id => new KIID { Value = id.Value }));
        var marker = new ErcMarker { ErrorType = ErcRule.ErcetPinNotConnected, Position = anchor.Position.Clone(),
            SheetSpecificPath = rootPath.Clone(), MainItemSheetPath = rootPath.Clone() };
        marker.Items.Add(new KIID { Value = anchor.Id.Value });
        string comment = "Accepted in the engineering XML review";
        xmlErc.Exclusions.Add(new ErcExclusion { Marker = marker, Comment = comment });
        cursor = session.Inspect().Sequence;
        await File.WriteAllBytesAsync(designPath, Encoding.UTF8.GetBytes(SchematicDesignXml.Write(WithErc(xmlDesign, xmlErc), [])), token);
        var xmlNative = await Synchronized("xml-apply", cursor, xmlErc);
        var xmlApplied = Read();
        OnlyErcChanged("The XML apply", beforeXml.Electrical.Hierarchy.Data, xmlNative.Electrical.Hierarchy.Data, xmlErc);
        await SameOutsideSchematic("xml-apply", xmlDesign, xmlApplied);
        var xmlApplication = store.Read()!.State.LastSynchronization!;
        Assert.IsTrue(xmlApplication.NativeMutationCommitted, "The XML ERC edit must be one committed native change.");
        Assert.AreEqual(beforeXml.State.Revision.Epoch, xmlNative.State.Revision.Epoch);
        Assert.AreEqual(beforeXml.State.Revision.Sequence + 1, xmlNative.State.Revision.Sequence,
            "The saved XML ERC edit must advance the native revision by exactly one commit.");
        Assert.IsTrue(xmlApplication.NativeFilesSaved);
        var liveExclusion = Erc(xmlNative.Electrical.Hierarchy.Data).Exclusions.Single();
        Assert.AreEqual(marker, liveExclusion.Marker, "KiCad must keep the exact marker references from XML.");
        Assert.AreEqual(comment, liveExclusion.Comment);

        // Undoing the applied XML edit in KiCad reaches XML too; redo restores it.
        var xmlUndone = await History("xml-undo", "z", setupErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(beforeXml.Electrical.Hierarchy.Data, xmlUndone.Electrical.Hierarchy.Data, token),
            "Undoing the XML edit must restore the complete native design.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(xmlDesign.Schematic, Read().Schematic, token),
            "The XML must return to the design it had before the XML edit.");
        await SameOutsideSchematic("xml-undo", xmlDesign, Read());
        var xmlRedone = await History("xml-redo", "y", xmlErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(xmlNative.Electrical.Hierarchy.Data, xmlRedone.Electrical.Hierarchy.Data, token),
            "Redoing the XML edit must restore the complete native design it applied.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(xmlApplied.Schematic, Read().Schematic, token),
            "The XML must return to the design the XML edit applied.");
        await SameOutsideSchematic("xml-redo", xmlDesign, Read());
        Assert.AreEqual(liveExclusion, Erc(xmlRedone.Electrical.Hierarchy.Data).Exclusions.Single());

        // The rendered ERC dialog deletes markers outside any edit: Run ERC replaces every marker,
        // including the one the XML edit added. Undo and redo of that edit must not touch the
        // deleted marker; they return the exclusion by its exact references.
        await OpenErcDialog();
        var xmlMarker = (await Markers(markers => markers.Any(m => m.Excluded))).Single(m => m.Excluded);
        Assert.AreEqual(marker, xmlMarker.Marker, "The XML exclusion must be on a live marker before Run ERC.");
        var beforeRun = await Capture();
        // Opening the checker focuses its real Run ERC button.
        NativeKeyboard.SchematicShortcut(display, processId, "Return", "Electrical Rules Checker", false, false);
        var checkedMarkers = await Markers(markers => markers.Count(m => m.Excluded) == 1
            && markers.Single(m => m.Excluded).Id.Value != xmlMarker.Id.Value);
        Assert.AreEqual(marker, checkedMarkers.Single(m => m.Excluded).Marker, "Run ERC keeps the XML exclusion's exact references.");
        var afterRun = await Capture();
        Assert.AreEqual(beforeRun.State.Revision, afterRun.State.Revision, "Running the checks is not an edit.");
        Assert.IsTrue(SchematicErcSettingsValidation.Same(xmlErc, Erc(afterRun.Electrical.Hierarchy.Data)));
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-run.png"), token);
        var runUndone = await History("run-undo", "z", setupErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(xmlUndone.Electrical.Hierarchy.Data, runUndone.Electrical.Hierarchy.Data, token),
            "Undo after Run ERC must restore the same native design as before.");
        var runRedone = await History("run-redo", "y", xmlErc);
        Assert.AreEqual(liveExclusion, Erc(runRedone.Electrical.Hierarchy.Data).Exclusions.Single());
        await SameOutsideSchematic("run-redo", xmlDesign, Read());

        // Delete Marker on the XML-added exclusion is itself one undoable edit. With only
        // exclusions shown, that exclusion is the first row of the retained 1280x900 dialog.
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Electrical Rules Checker", false,
            clickFromLeft: 437, clickFromBottom: 72);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Electrical Rules Checker", false,
            clickFromLeft: 250, clickFromTop: 85);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-delete-selected.png"), token);
        var withoutExclusion = xmlErc.Clone(); withoutExclusion.Exclusions.Clear();
        var beforeDelete = await Capture();
        cursor = session.Inspect().Sequence;
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Electrical Rules Checker", false,
            clickFromLeft: 78, clickFromBottom: 25);
        var deleted = await Synchronized("dialog-delete-marker", cursor, withoutExclusion);
        Assert.AreEqual(beforeDelete.State.Revision.Epoch, deleted.State.Revision.Epoch);
        Assert.AreEqual(beforeDelete.State.Revision.Sequence + 1, deleted.State.Revision.Sequence,
            "Deleting the exclusion in the ERC dialog must add exactly one revision.");
        OnlyErcChanged("Delete Marker", beforeDelete.Electrical.Hierarchy.Data, deleted.Electrical.Hierarchy.Data, withoutExclusion);
        await SameOutsideSchematic("dialog-delete-marker", xmlDesign, Read());
        Assert.IsEmpty((await Markers(_ => true)).Where(m => m.Excluded));
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-deleted.png"), token);
        var deleteUndone = await History("delete-undo", "z", xmlErc);
        Assert.AreEqual(liveExclusion, Erc(deleteUndone.Electrical.Hierarchy.Data).Exclusions.Single(),
            "Undoing Delete Marker returns the exact XML exclusion.");
        await History("delete-redo", "y", withoutExclusion);
        var restoredAgain = await History("delete-undo-again", "z", xmlErc);
        Assert.AreEqual(liveExclusion, Erc(restoredAgain.Electrical.Hierarchy.Data).Exclusions.Single());
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(xmlNative.Electrical.Hierarchy.Data, restoredAgain.Electrical.Hierarchy.Data, token),
            "The final undo returns the complete native design the XML edit applied.");
        await SameOutsideSchematic("delete-undo-again", xmlDesign, Read());
        await CloseErcDialog();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, original = originalErc.ToString(), setup = setupErc.ToString(), xml = xmlErc.ToString(),
            nativeSetupEditPublished = true, setupEditOneRevision = true, nativeUndoRedoPublished = true,
            xmlEditAppliedAsOneCommit = true, xmlUndoRedoPublished = true, exactExclusionReferencesKept = true,
            runErcThenUndoRedoSafe = true, dialogDeleteMarkerUndoRedo = true, everyHistoryStepOneRevision = true,
            otherDesignContentUnchangedAsCanonicalXml = true
        }), token);
        return xmlErc;

        SchematicDesign Read() => SchematicDesignXml.Read(File.ReadAllText(designPath), []);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        ErcSettings Erc(Kiapi.Schematic.Types.SchematicHierarchyData data) =>
            data.Instances.Single(s => s.Metadata.Document.Equals(root)).Metadata.ErcSettings;

        // Keyboard undo or redo is exactly one revision, KiCad survives it, and the worker
        // publishes it with KiCad and the XML equal.
        async Task<CheckedSchematicState> History(string stage, string key, ErcSettings expected)
        {
            ulong before = session.Inspect().Sequence;
            var prior = await Capture();
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            var native = await Synchronized("history-" + stage, before, expected);
            Assert.IsFalse(System.Diagnostics.Process.GetProcessById(processId).HasExited, stage + ": KiCad must survive the history step.");
            Assert.AreEqual(prior.State.Revision.Epoch, native.State.Revision.Epoch, stage + " must keep the document session.");
            Assert.AreEqual(prior.State.Revision.Sequence + 1, native.State.Revision.Sequence, stage + " must be exactly one revision.");
            return native;
        }

        // The rendered Inspect menu: Bus Syntax Help, then Electrical Rules Checker (GTK skips the separator).
        async Task OpenErcDialog()
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            NativeKeyboard.SchematicShortcut(display, processId, "i", controlKey: false, altKey: true);
            foreach (string key in new[] { "Home", "Down", "Return" })
                NativeKeyboard.SchematicShortcut(display, processId, key, controlKey: false, focusCanvas: false);
            await WindowState("Electrical Rules Checker", true);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-automatic-erc-dialog.png"), token);
        }

        async Task CloseErcDialog()
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Electrical Rules Checker", false,
                clickFromRight: 150, clickFromBottom: 25);
            await WindowState("Electrical Rules Checker", false);
        }

        async Task WindowState(string title, bool shown)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(10));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, title) != shown)
            { await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500); }
        }

        // The live ERC markers of every screen, once the rendered action has produced them.
        async Task<IReadOnlyList<SchematicTrackingErcMarker>> Markers(Func<IReadOnlyList<SchematicTrackingErcMarker>, bool> accept)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            int delay = 25;
            while (true)
            {
                try
                {
                    var markers = (await client.InvokeAsync<SchematicTrackingReadErcMarkers, SchematicTrackingErcMarkers>(
                        new() { Document = root.Clone() }, wait.Token)).Markers;
                    if (accept(markers)) return markers;
                }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
            }
        }

        // The worker is settled when it is watching after the change, has no pending
        // operation, and KiCad and the published XML both carry the expected settings.
        async Task<CheckedSchematicState> Synchronized(string stage, ulong after, ErcSettings expected)
        {
            var status = session.Inspect();
            // One worker synchronization with a checked save takes seconds; this failure
            // ceiling keeps a lost edit from consuming the other project's session time.
            using var stageLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
            stageLimit.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                while (true)
                {
                    Assert.IsFalse(status.Phase is AutomaticDesignPhase.Paused or AutomaticDesignPhase.Stopped,
                        stage + ": " + status.ErrorCode + ": " + status.ErrorMessage);
                    if (status.Phase == AutomaticDesignPhase.Watching && status.Sequence > after
                        && store.Read() is { State.HasPendingWork: false })
                    {
                        var native = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                            { Document = root.Clone(), ProcessEpoch = client.Epoch }, stageLimit.Token);
                        var design = Read();
                        if (SchematicErcSettingsValidation.Same(expected, Erc(native.Electrical.Hierarchy.Data))
                            && SchematicErcSettingsValidation.Same(expected, Erc(design.Schematic))
                            && SchematicHierarchyDelta.Plan(native.Electrical.Hierarchy.Data, design.Schematic, token).Count == 0)
                            return native;
                    }
                    status = await session.WaitAsync(status.Sequence, stageLimit.Token);
                }
            }
            catch (OperationCanceledException)
            {
                string xml;
                try { xml = Erc(Read().Schematic).ToString(); }
                catch (Exception error) { xml = "unreadable: " + error.Message; }
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-" + stage + "-timeout.json"),
                    JsonSerializer.Serialize(new { status = session.Inspect(), recovery = store.Read(),
                        expected = expected.ToString(), xml }), CancellationToken.None);
                throw;
            }
        }

        void OnlyErcChanged(string what, Kiapi.Schematic.Types.SchematicHierarchyData before,
            Kiapi.Schematic.Types.SchematicHierarchyData after, ErcSettings expected)
        {
            var operations = SchematicHierarchyDelta.Plan(before, after, token);
            Assert.HasCount(1, operations, what + " must change only the project ERC settings.");
            Assert.IsTrue(SchematicErcSettingsValidation.Same(expected, operations[0].SetErcSettings),
                what + " must carry exactly the expected ERC settings.");
        }

        // Engineering intent, bindings and part symbols must be unchanged. Both sides are
        // re-serialized by the same canonical XML writer, so this compares content, not file bytes.
        async Task SameOutsideSchematic(string stage, SchematicDesign before, SchematicDesign after)
        {
            string expected = SchematicDesignXml.Write(before with { Schematic = after.Schematic }, []);
            string actual = SchematicDesignXml.Write(after, []);
            if (expected == actual) return;
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-" + stage + "-expected.xml"), expected, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-" + stage + "-actual.xml"), actual, token);
            Assert.Fail("An ERC synchronization changed design content outside the schematic settings; both versions are retained.");
        }
    }

    // Every automatic synchronization saves KiCad's checked files. The saved project must hold
    // the complete ERC section, and a fresh KiCad process that opens a copy of the saved files
    // (so the project is read from disk, not kept from memory) must restore it exactly. Then the
    // running editor is reverted and a new worker is reattached through the public tools: revert
    // reloads the schematic files but keeps this process's loaded project settings, so it proves
    // the reattachment, not the disk round trip.
    private static async Task VerifyAutomaticErcReload(StdioMcpFixture publicHost, NativeClient client,
        DocumentSpecifier root, DesignRecoveryStore store, string designPath, ErcSettings saved, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        Assert.IsFalse(store.Read()!.State.HasPendingWork, "The stopped worker must leave no pending operation before reload.");
        var exclusion = saved.Exclusions.Single();
        using (var project = JsonDocument.Parse(await File.ReadAllBytesAsync(ProjectFile(root), token)))
        {
            var erc = project.RootElement.GetProperty("erc");
            var rule = saved.RuleSeverities.Single(r => r.RuleType == ErcRule.ErcetPinNotConnected);
            Assert.AreEqual(rule.Severity == RuleSeverity.RsWarning ? "warning" : "error",
                erc.GetProperty("rule_severities").GetProperty("pin_not_connected").GetString());
            // Every cell of the pin conflict matrix, indexed by native pin type.
            var pinMap = erc.GetProperty("pin_map");
            int types = saved.PinMap.Select(cell => cell.First).Distinct().Count();
            Assert.AreEqual(types, pinMap.GetArrayLength());
            foreach (var cell in saved.PinMap)
            {
                var row = pinMap[(int)cell.First - 1];
                Assert.AreEqual(types, row.GetArrayLength());
                Assert.AreEqual((int)cell.Conflict - 1, row[(int)cell.Second - 1].GetInt32(),
                    $"The saved pin rule {cell.First}/{cell.Second} must be the synchronized one.");
            }
            // The exclusion exactly as KiCad writes it: every marker reference and the comment.
            var exclusions = erc.GetProperty("erc_exclusions");
            Assert.AreEqual(1, exclusions.GetArrayLength());
            Assert.AreEqual(exclusion, Google.Protobuf.JsonParser.Default.Parse<ErcExclusion>(exclusions[0].GetRawText()),
                "The saved exclusion must keep the exact marker references and comment.");
        }
        // The native reader is the oracle for every persisted rule severity: a fresh process
        // reads the saved project from disk and must restore exactly the synchronized settings.
        var reopenedErc = await ReopenSavedErc(root, display, evidence, instanceId, token);
        Assert.IsTrue(SchematicErcSettingsValidation.Same(saved, reopenedErc),
            "A fresh KiCad process must restore every saved ERC rule, pin rule and exclusion from disk.");
        Assert.AreEqual(exclusion, reopenedErc.Exclusions.Single(), "Reopening must keep the exact exclusion references.");

        var beforeReload = await Capture();
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var reloaded = await Capture();
        Assert.AreNotEqual(beforeReload.State.Revision.Epoch, reloaded.State.Revision.Epoch, "Reverting starts a new native document session.");
        Assert.IsTrue(SchematicErcSettingsValidation.Same(saved, Erc(reloaded.Electrical.Hierarchy.Data)),
            "The reverted editor must keep the saved ERC policy and resolve the saved exclusions onto its reloaded markers.");
        Assert.AreEqual(exclusion, Erc(reloaded.Electrical.Hierarchy.Data).Exclusions.Single());

        var beforeReattach = store.Read()!;
        RequireToolSuccess(await publicHost.Tool("kicad_design_recovery_reattach", new { instanceId,
            recoveryPath = store.StatePath, expectedRevisionToken = beforeReattach.RevisionToken,
            expectedDocumentEpoch = reloaded.State.Revision.Epoch }));
        var start = await publicHost.Tool("kicad_design_automatic_sync_start", new { instanceId,
            recoveryPath = store.StatePath, designPath, expectedRecoveryRevision = store.Read()!.RevisionToken });
        RequireToolSuccess(start);
        string sessionId = start.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
        var status = start.GetProperty("structuredContent").GetProperty("status");
        while (status.GetProperty("phase").GetString() != "Watching")
        {
            Assert.IsFalse(status.GetProperty("phase").GetString() is "Paused" or "Stopped", status.GetRawText());
            var next = await publicHost.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId,
                afterSequence = status.GetProperty("sequence").GetUInt64() });
            RequireToolSuccess(next); status = next.GetProperty("structuredContent").GetProperty("status");
        }
        var settled = await Capture();
        var design = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsFalse(store.Read()!.State.HasPendingWork);
        Assert.AreEqual(reloaded.State.Revision.Epoch, settled.State.Revision.Epoch);
        Assert.IsTrue(SchematicErcSettingsValidation.Same(saved, Erc(settled.Electrical.Hierarchy.Data)));
        Assert.IsTrue(SchematicErcSettingsValidation.Same(saved, Erc(design.Schematic)),
            "The reattached worker must keep the saved ERC settings in XML.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(settled.Electrical.Hierarchy.Data, design.Schematic, token));
        Assert.IsTrue(SchematicElectricalComparison.Compare(design, settled.Electrical, [], token).ConnectivityEquivalent);
        var stop = await publicHost.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId });
        RequireToolSuccess(stop);
        Assert.AreEqual("Stopped", stop.GetProperty("structuredContent").GetProperty("status").GetProperty("phase").GetString());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-reload.json"), JsonSerializer.Serialize(new
        {
            instanceId, previousEpoch = beforeReload.State.Revision.Epoch, reloadedEpoch = reloaded.State.Revision.Epoch,
            savedErcSectionVerified = true, freshProcessReopenVerified = true, revertedNativeVerified = true,
            publicReattachVerified = true, reattachedXmlVerified = true
        }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        ErcSettings Erc(Kiapi.Schematic.Types.SchematicHierarchyData data) =>
            data.Instances.Single(s => s.Metadata.Document.Equals(root)).Metadata.ErcSettings;
    }

    // Opens a copy of the saved design and project files in a fresh KiCad process, so its project
    // settings are read from disk, and returns the ERC settings that process restored.
    private static async Task<ErcSettings> ReopenSavedErc(DocumentSpecifier root, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        string executable = Path.Combine(FindRoot(), "automation", "artifacts", "native", "kicad", "kicad");
        string scratch = Directory.CreateTempSubdirectory("kicad-erc-reopen-").FullName;
        string copy = Directory.CreateDirectory(Path.Combine(scratch, "project")).FullName;
        // Only the saved design, project and library files: never the running editor's
        // configuration, cache, locks, socket or history.
        foreach (string file in Directory.EnumerateFiles(root.Project.Path, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root.Project.Path, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts[0] is "config" or "cache" || parts.Any(part => part.StartsWith('.') || part.StartsWith('~')))
                continue;
            if (Path.GetExtension(file) is not (".kicad_sch" or ".kicad_pro" or ".kicad_prl" or ".kicad_sym")
                && Path.GetFileName(file) is not ("sym-lib-table" or "fp-lib-table"))
                continue;
            string target = Path.Combine(copy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        string project = Path.Combine(copy, root.Project.Name + ".kicad_pro");
        Assert.IsTrue(File.Exists(project), "The saved project file must be copied for the fresh process.");
        string reopenId = Guid.NewGuid().ToString("D");
        string socket = Path.Combine(scratch, "api.sock");
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            WorkingDirectory = copy, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["DISPLAY"] = display;
        start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(scratch, "cache");
        // The settings trace names every settings file the process reads, so the retained log
        // shows the project file came from the copy on disk.
        start.Environment["WXTRACE"] = "KICAD_SETTINGS";
        start.Environment["KICAD_ENABLE_WXTRACE"] = "1";
        string nativeLog = Path.Combine(evidence, instanceId + "-erc-reopen-native.log");
        foreach (string argument in new[] { "--new", "--automation", reopenId, "--api-socket", socket,
            "--automation-log", nativeLog, "--software-rendering", project })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = Capture(process.StandardOutput, Path.Combine(evidence, instanceId + "-erc-reopen.stdout.log"));
        var errors = Capture(process.StandardError, Path.Combine(evidence, instanceId + "-erc-reopen.stderr.log"));
        ErcSettings restored;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(scratch, "registry"));
            int delay = 25;
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(process.HasExited, "The fresh KiCad process exited before serving requests.");
                try { await registry.AttachAsync("ipc://" + socket, reopenId, limit.Token); break; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 500);
            }
            var reopened = registry.Client(reopenId);
            var opened = await reopened.OpenRootSchematicAsync(Path.ChangeExtension(project, ".kicad_sch"), limit.Token);
            Assert.AreEqual(root.Project.Name, opened.Document.Project.Name, "The fresh process must open the copied project.");
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(copy), Path.TrimEndingDirectorySeparator(opened.Document.Project.Path),
                "The fresh process must open the copy of the saved files, not the running editor's project.");
            var metadata = await reopened.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
                new() { Document = opened.Document }, limit.Token);
            Assert.IsNotNull(metadata.Metadata.ErcSettings, "The fresh process must report the ERC settings it loaded.");
            restored = metadata.Metadata.ErcSettings;
        }
        finally
        {
            // Only this disposable process and its copied files are removed.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, errors);
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        // The settings trace names the project path it loads, then the file it read by name.
        string trace = await File.ReadAllTextAsync(nativeLog, token);
        int load = trace.IndexOf("Load project " + project + "\n", StringComparison.Ordinal);
        Assert.IsTrue(load >= 0, "The fresh process must load the copied project file " + project + ".");
        StringAssert.Contains(trace[load..], "Loaded <" + Path.GetFileName(project) + "> with schema",
            "The fresh process must read the project settings from the copied project file.");
        return restored;
    }

    private static string ProjectFile(DocumentSpecifier root) =>
        Path.Combine(root.Project.Path, root.Project.Name + ".kicad_pro");

    // ERC settings are project-wide; every sheet instance carries the same copy.
    private static SchematicDesign WithErc(SchematicDesign design, ErcSettings erc)
    {
        var schematic = design.Schematic.Clone();
        foreach (var screen in schematic.Instances) screen.Metadata.ErcSettings = erc.Clone();
        return design with { Schematic = schematic };
    }
}
