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
        // edit, four keyboard undo/redo steps, one XML apply and a save/reload
        // reattachment, each a full worker synchronization with a checked native save.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(180));
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
        await VerifyAutomaticErcReload(publicHost, client, root, store, designPath, savedErc, evidence, instanceId, deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-sync-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeCommitPublishedWithoutApplyCall = true, xmlSaveAppliedWithoutApplyCall = true,
            invalidXmlPreserved = true, correctedXmlResumed = true, competingOwnerRejected = true,
            stopPreservedEditor = true, missedEditRecoveredAfterReattach = true,
            publicToolsQualified = true, ercNativeSetupEditPublished = true, ercXmlEditApplied = true,
            ercNativeUndoRedoPublished = true, ercSavedReloadedAndReattached = true, crossPlatformReady = false
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
        var setupNative = await Synchronized("native-setup", cursor, setupErc);
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
        var setupUndone = await History("z", originalErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(original.Electrical.Hierarchy.Data, setupUndone.Electrical.Hierarchy.Data, token),
            "Undoing the Setup edit must restore the complete native design.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(originalDesign.Schematic, Read().Schematic, token),
            "The XML must return to the design it had before the Setup edit.");
        await History("y", setupErc);

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
        OnlyErcChanged("The XML apply", beforeXml.Electrical.Hierarchy.Data, xmlNative.Electrical.Hierarchy.Data, xmlErc);
        await SameOutsideSchematic("xml-apply", xmlDesign, Read());
        var xmlApplication = store.Read()!.State.LastSynchronization!;
        Assert.IsTrue(xmlApplication.NativeMutationCommitted, "The XML ERC edit must be one committed native change.");
        Assert.IsTrue(xmlApplication.NativeFilesSaved);
        var liveExclusion = Erc(xmlNative.Electrical.Hierarchy.Data).Exclusions.Single();
        Assert.AreEqual(marker, liveExclusion.Marker, "KiCad must keep the exact marker references from XML.");
        Assert.AreEqual(comment, liveExclusion.Comment);

        // Undoing the applied XML edit in KiCad reaches XML too; redo restores it.
        var xmlUndone = await History("z", setupErc);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(beforeXml.Electrical.Hierarchy.Data, xmlUndone.Electrical.Hierarchy.Data, token),
            "Undoing the XML edit must restore the complete native design.");
        var xmlRedone = await History("y", xmlErc);
        Assert.AreEqual(liveExclusion, Erc(xmlRedone.Electrical.Hierarchy.Data).Exclusions.Single());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-erc-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, original = originalErc.ToString(), setup = setupErc.ToString(), xml = xmlErc.ToString(),
            nativeSetupEditPublished = true, nativeUndoRedoPublished = true, xmlEditAppliedAsOneCommit = true,
            xmlUndoRedoPublished = true, exactExclusionReferencesKept = true, otherDesignContentUnchanged = true
        }), token);
        return xmlErc;

        SchematicDesign Read() => SchematicDesignXml.Read(File.ReadAllText(designPath), []);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        ErcSettings Erc(Kiapi.Schematic.Types.SchematicHierarchyData data) =>
            data.Instances.Single(s => s.Metadata.Document.Equals(root)).Metadata.ErcSettings;

        async Task<CheckedSchematicState> History(string key, ErcSettings expected)
        {
            ulong before = session.Inspect().Sequence;
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            return await Synchronized("history-" + key, before, expected);
        }

        // The worker is settled when it is watching after the change, has no pending
        // operation, and KiCad and the published XML both carry the expected settings.
        async Task<CheckedSchematicState> Synchronized(string stage, ulong after, ErcSettings expected)
        {
            var status = session.Inspect();
            try
            {
                while (true)
                {
                    Assert.IsFalse(status.Phase is AutomaticDesignPhase.Paused or AutomaticDesignPhase.Stopped,
                        stage + ": " + status.ErrorCode + ": " + status.ErrorMessage);
                    if (status.Phase == AutomaticDesignPhase.Watching && status.Sequence > after
                        && store.Read() is { State.HasPendingWork: false })
                    {
                        var native = await Capture(); var design = Read();
                        if (SchematicErcSettingsValidation.Same(expected, Erc(native.Electrical.Hierarchy.Data))
                            && SchematicErcSettingsValidation.Same(expected, Erc(design.Schematic))
                            && SchematicHierarchyDelta.Plan(native.Electrical.Hierarchy.Data, design.Schematic, token).Count == 0)
                            return native;
                    }
                    status = await session.WaitAsync(status.Sequence, token);
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

        // Engineering intent, bindings and part symbols are compared byte for byte.
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

    // Every automatic synchronization saves KiCad's checked files. Reload them from
    // disk, reattach the saved design through the public tools and let a new worker
    // settle: the reloaded project, KiCad and the XML all keep the XML-applied ERC.
    private static async Task VerifyAutomaticErcReload(StdioMcpFixture publicHost, NativeClient client,
        DocumentSpecifier root, DesignRecoveryStore store, string designPath, ErcSettings saved, string evidence,
        string instanceId, CancellationToken token)
    {
        Assert.IsFalse(store.Read()!.State.HasPendingWork, "The stopped worker must leave no pending operation before reload.");
        var exclusion = saved.Exclusions.Single();
        using (var project = JsonDocument.Parse(await File.ReadAllBytesAsync(ProjectFile(root), token)))
        {
            var erc = project.RootElement.GetProperty("erc");
            var rule = saved.RuleSeverities.Single(r => r.RuleType == ErcRule.ErcetPinNotConnected);
            Assert.AreEqual(rule.Severity == RuleSeverity.RsWarning ? "warning" : "error",
                erc.GetProperty("rule_severities").GetProperty("pin_not_connected").GetString());
            foreach (var cell in saved.PinMap.Where(c => c.First == c.Second
                && c.First is ElectricalPinType.EptInput or ElectricalPinType.EptOutput))
                Assert.AreEqual((int)cell.Conflict - 1, erc.GetProperty("pin_map")[(int)cell.First - 1][(int)cell.Second - 1].GetInt32());
            var exclusions = erc.GetProperty("erc_exclusions");
            Assert.AreEqual(1, exclusions.GetArrayLength());
            Assert.AreEqual(exclusion.Comment, exclusions[0].GetProperty("comment").GetString());
        }
        var beforeReload = await Capture();
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var reloaded = await Capture();
        Assert.AreNotEqual(beforeReload.State.Revision.Epoch, reloaded.State.Revision.Epoch, "Reloading starts a new native document session.");
        Assert.IsTrue(SchematicErcSettingsValidation.Same(saved, Erc(reloaded.Electrical.Hierarchy.Data)),
            "The reloaded project must restore the saved ERC policy and exclusions.");
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
            savedProjectVerified = true, reloadedNativeVerified = true, publicReattachVerified = true, reattachedXmlVerified = true
        }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        ErcSettings Erc(Kiapi.Schematic.Types.SchematicHierarchyData data) =>
            data.Instances.Single(s => s.Metadata.Document.Equals(root)).Metadata.ErcSettings;
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
