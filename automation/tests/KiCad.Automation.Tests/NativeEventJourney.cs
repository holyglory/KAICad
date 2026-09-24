using System.Diagnostics;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeEvents(NativeClient client, NativeClient otherClient,
        DocumentSpecifier document, string textId, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        await VerifyNativeEventDelivery(client, otherClient, document, textId, processId, display, evidence,
            instanceId, token);
        // Direct native owners are editor code paths, not per-project state: one editor of the
        // pair drives them, after its event observers have been released.
        var session = await client.HandshakeAsync(token);
        var other = await otherClient.HandshakeAsync(token);
        Assert.AreNotEqual(session.InstanceId, other.InstanceId, "Each editor must identify its own instance.");
        if (string.CompareOrdinal(session.InstanceId, other.InstanceId) < 0)
        {
            await VerifyDirectOwnerTracking(client, document, processId, display, evidence, instanceId, token);
            // What tracking compares on the largest demo design is measured by the native test
            // binary after the rendered steps, never alongside them, so neither slows the other.
            await MeasureTrackingCostOnLargestDemo(evidence, instanceId, token);
        }
    }

    private static async Task VerifyNativeEventDelivery(NativeClient client, NativeClient otherClient,
        DocumentSpecifier document, string textId, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        var session = await client.HandshakeAsync(token);
        var other = await otherClient.HandshakeAsync(token);
        Assert.AreNotEqual(session.EventEndpoint, other.EventEndpoint);
        Assert.AreNotEqual(session.EventEpoch, other.EventEpoch);
        using var first = new NativeEventSubscription(session);
        using var second = new NativeEventSubscription(session);
        using var isolated = new NativeEventSubscription(other);
        var initial = await Task.WhenAll(Receive(first), Receive(second), Receive(isolated));
        Assert.IsTrue(initial.All(value => value.Disposition == NativeEventDisposition.InitialStateRequired));
        await using var intake = await NativeRecoveryIntakeProbe.StartAsync(client, document, evidence, instanceId, token);
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var query = new GetItemsById { Header = new() { Document = document } };
        query.Items.Add(new KIID { Value = textId });
        async Task<SchematicText> Text() =>
            (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SchematicText>();
        var original = await Text(); var moved = original.Clone(); moved.Text.Position.XNm += 2540000;
        var batch = new ApplySchematicItemBatch
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch,
            ExpectedRevision = new() { Epoch = journal.DocumentEpoch, Sequence = journal.Sequence },
            OperationId = Guid.NewGuid().ToString("D"), OriginId = Guid.NewGuid().ToString("D"),
            Description = "Native event delivery fixture"
        };
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(moved) });
        intake.BlockNextPersistence();
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var committed = await Change(1);
        Assert.AreEqual(batch.OperationId, committed.SchematicCommit.Change.OperationId);
        Assert.AreEqual(batch.OriginId, committed.SchematicCommit.Change.OriginId);
        Assert.AreEqual(moved, await Text());
        // An operation-ID retry returns its original result without publishing
        // another edit; a rejected stale operation also leaves the event head.
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(rejected.Message, "Stale document revision");
        Assert.AreEqual(journal.Sequence, (await client.InvokeAsync<ReadSchematicChangeJournal,
            SchematicChangeJournal>(new() { Document = document }, token)).Sequence);
        var unchangedHead = await Receive(first);
        Assert.AreEqual(NativeEventDisposition.Heartbeat, unchangedHead.Disposition);
        Assert.AreEqual(committed.Sequence, unchangedHead.Event.Sequence);
        Assert.AreEqual(moved, await Text());
        var otherHeartbeat = await Receive(isolated);
        Assert.AreEqual(NativeEventDisposition.Heartbeat, otherHeartbeat.Disposition);
        Assert.AreEqual(initial[2].Event.Sequence, otherHeartbeat.Event.Sequence);

        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await Change(2);
        Assert.AreEqual(original, await Text());
        NativeKeyboard.SchematicShortcut(display, processId, "y");
        await Change(3);
        Assert.AreEqual(moved, await Text());
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await Change(2);
        Assert.AreEqual(original, await Text());

        await intake.VerifyStopAndMcpDisconnect();

        // Cancellation releases only this subscriber. The same process, dirty
        // state and command channel remain available, and another observer works.
        var saveState = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);
        using (var cancelled = new NativeEventSubscription(session))
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await Receive(cancelled);
            var waiting = cancelled.ReceiveAsync(cancel.Token);
            cancel.Cancel();
            try { await waiting; Assert.Fail("The cancelled observer returned a successful event."); }
            catch (OperationCanceledException) { Assert.IsTrue(waiting.IsCanceled); }
        }
        Assert.AreEqual(session.Epoch, (await client.HandshakeAsync(token)).Epoch);
        Assert.AreEqual(saveState, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token));
        using var recovered = new NativeEventSubscription(await client.HandshakeAsync(token));
        Assert.AreEqual(NativeEventDisposition.InitialStateRequired, (await Receive(recovered)).Disposition);

        // A deliberately stale resumed cursor must request recovery even if the
        // missing last change is followed only by cursor heartbeats.
        using var missed = new NativeEventSubscription(session, initial[0].Event.Sequence);
        Assert.AreEqual(NativeEventDisposition.RecoveryRequired, (await Receive(missed)).Disposition);

        async Task<NativeEventDelivery> Receive(NativeEventSubscription subscription)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return await subscription.ReceiveAsync(timeout.Token);
        }

        async Task<AutomationEvent> Change(int kind)
        {
            async Task<NativeEventDelivery> Next(NativeEventSubscription subscription)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var delivery = await subscription.ReceiveAsync(timeout.Token);
                    if (delivery.Disposition == NativeEventDisposition.Heartbeat) continue;
                    return delivery;
                }
            }
            var deliveries = await Task.WhenAll(Next(first), Next(second));
            Assert.IsTrue(deliveries.All(value => value.Disposition == NativeEventDisposition.Change));
            Assert.AreEqual(deliveries[0].Event, deliveries[1].Event);
            var notification = deliveries[0].Event;
            Assert.AreEqual(document, notification.SchematicCommit.Document);
            Assert.AreEqual(journal.DocumentEpoch, notification.SchematicCommit.Revision.Epoch);
            Assert.AreEqual(++journal.Sequence, notification.SchematicCommit.Revision.Sequence);
            Assert.AreEqual(kind, (int)notification.SchematicCommit.Change.Kind);
            if (kind != 1)
            {
                Assert.IsEmpty(notification.SchematicCommit.Change.OperationId);
                Assert.IsEmpty(notification.SchematicCommit.Change.OriginId);
            }
            Assert.IsFalse(notification.SchematicCommit.TrackingComplete);
            await File.WriteAllTextAsync(Path.Combine(evidence,
                instanceId + "-native-event-" + notification.Sequence + ".json"), JsonFormatter.Default.Format(notification), token);
            await intake.WaitForRevision(notification.SchematicCommit.Revision.Sequence);
            return notification;
        }
    }

    /// <summary>
    /// Native editor actions that change the saved design outside an ordinary item commit must
    /// make older AI requests stale, and cancelled or unchanged ones must not (p0bd2c0d9e475f181).
    /// Drives the rendered Symbol Properties dialog with its fields grid (also on a symbol that a
    /// duplication or a move still carries), the Sheet Properties dialog, the hierarchy pane's
    /// top-level sheet actions, Page Settings, Schematic Setup, Annotate Schematic and Place >
    /// Import Sheet, and compares the exact persisted state digest, journal revision and modified
    /// flag after each action.  The change-tracking oracle requires every proven owner's OneChange
    /// and Unchanged steps here, as statements this method always runs.  It also records what a
    /// lifecycle read costs on this fixture; the comparisons on the largest demo design are
    /// measured afterwards by the native test binary.
    /// </summary>
    private static async Task VerifyDirectOwnerTracking(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        const string symbolDialog = "Symbol Properties", sheetDialog = "Sheet Properties",
            newSheet = "New Top-Level Sheet", deleteSheet = "Delete Top-Level Sheet",
            sheetName = "Tracked Owner Sheet";
        var header = new ItemHeader { Document = document.Clone() };

        void Key(string key, string window, bool control = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, focusCanvas: false);
        async Task Failed(string step, string message)
        {
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}-failed.png"),
                CancellationToken.None);
            Assert.Fail(message);
        }
        async Task Window(string window, bool visible, string step)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, window) != visible)
                    await Task.Delay(50, deadline.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await Failed(step, $"The '{window}' window did not become {(visible ? "visible" : "hidden")} during {step}.");
            }
        }
        async Task<T> Settled<T>(Func<CancellationToken, Task<T>> read)
        {
            // A closing modal dialog briefly keeps the editor busy.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { return await read(limit.Token); }
                catch (NativeApiException error) when (error.Status == 7) { await Task.Delay(50, limit.Token); }
            }
        }
        Task<DocumentLifecycleState> State() => Settled(t =>
            client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document.Clone() }, t));
        Task<SchematicChangeJournal> Changes(DocumentLifecycleState since) => Settled(t =>
            client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            {
                Document = document.Clone(), DocumentEpoch = since.Revision.Epoch, AfterSequence = since.Revision.Sequence
            }, t));
        async Task<DocumentLifecycleState> Advanced(DocumentLifecycleState since, string step = "revision")
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    var current = await State();
                    if (current.Revision.Sequence != since.Revision.Sequence) return current;
                    await Task.Delay(50, limit.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await Failed(step, $"No revision followed revision {since.Revision.Sequence} within 5 s during {step}.");
                throw;
            }
        }
        async Task Unchanged(DocumentLifecycleState baseline, string step)
        {
            var after = await State();
            Assert.AreEqual(baseline.Revision, after.Revision, $"{step} must not create a revision.");
            Assert.AreEqual(baseline.StateSha256, after.StateSha256, $"{step} must not change the saved design state.");
            Assert.AreEqual(baseline.NativeContentDirty, after.NativeContentDirty, $"{step} must not mark the design modified.");
            Assert.IsEmpty((await Changes(baseline)).Changes, $"{step} must not add a journal change.");
        }
        async Task<SchematicChange> OneChange(DocumentLifecycleState baseline, string description, SchematicChange.Types.Kind kind)
        {
            var journal = await Changes(baseline);
            Assert.IsFalse(journal.ResetRequired);
            Assert.HasCount(1, journal.Changes, $"'{description}' must be exactly one revision.");
            var change = journal.Changes.Single();
            Assert.AreEqual(kind, change.Kind);
            Assert.AreEqual(description, change.Description);
            Assert.IsEmpty(change.OriginId, "A native user edit carries no automation origin.");
            Assert.IsFalse(journal.TrackingComplete, "Complete tracking is not claimed yet.");
            return change;
        }
        async Task<DocumentLifecycleState> Saved()
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
            var saved = await State();
            Assert.IsFalse(saved.NativeContentDirty, "Saving must leave a clean baseline.");
            return saved;
        }
        async Task Undo(DocumentLifecycleState edited, DocumentLifecycleState original, string step)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            var undone = await Advanced(edited);
            await OneChange(edited, "Undo", SchematicChange.Types.Kind.Undo);
            Assert.AreEqual(original.StateSha256, undone.StateSha256, $"Undoing {step} must restore the exact saved design state.");
        }

        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        // Start from a saved design so an unchanged action can be checked for the modified flag.
        var clean = await Saved();

        // A lifecycle read builds its digest from exactly the groups a whole-state tracker
        // compares, so each read performs one whole-state capture plus the request round trip.
        var samples = new List<double>();
        for (int sample = 0; sample < 7; sample++)
        {
            var timer = Stopwatch.StartNew();
            var observed = await State();
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.AreEqual(clean.StateSha256, observed.StateSha256, "Reading the state must not change it.");
        }
        samples.Sort();
        string cost = $"Lifecycle read over {clean.NativeFiles.Count - 1} screen file(s) and the project settings "
            + $"(one whole-state capture plus the request round trip, Debug build): median {samples[samples.Count / 2]:F1} ms, "
            + $"min {samples[0]:F1} ms, max {samples[^1]:F1} ms. A whole-state tracker (Page Settings, Import Sheet "
            + "and design block placement, and the other whole-state owners) captures twice per action, a cancelled "
            + "one included; Schematic Setup and the simulation settings capture the project settings and the first "
            + "top-level sheet twice; Symbol Properties, undoable Sheet Properties and the simulator tuner compare "
            + "only their staged items.";
        Console.WriteLine("Native tracking cost: " + cost);
        await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-owner-capture-cost.txt"), cost + Environment.NewLine, token);

        var screen = await Settled(t => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document.Clone() }, t));
        async Task Select(KIID item)
        {
            // A background click gives the canvas keyboard focus, then the exact item is selected
            // through the API so no pointer position chooses it.
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false, focusCanvas: true);
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header.Clone() }, token);
            var select = new AddToSelection { Header = header.Clone() }; select.Items.Add(item.Clone());
            await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        }
        async Task OpenProperties(KIID item, string dialog, string step)
        {
            await Select(item);
            Key("e", "Schematic Editor");
            await Window(dialog, true, step);
            // The dialog opens its first value cell editor through queued events.
            await Task.Delay(500, token);
        }
        async Task Cancel(string dialog, string step)
        {
            // The first Escape may only close the open cell editor.
            for (int attempt = 0; attempt < 3 && NativeKeyboard.HasWindow(display, processId, dialog); attempt++)
            {
                Key("Escape", dialog);
                await Task.Delay(300, token);
            }
            await Window(dialog, false, step);
        }
        async Task AppendToFirstValue(string dialog, string step)
        {
            // F2 opens the first value cell editor if the dialog has not already done so; End keeps
            // the existing text and appends to it.
            Key("F2", dialog); Key("End", dialog); Key("7", dialog);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}.png"), token);
            Key("Return", dialog, control: true);
            await Window(dialog, false, step);
        }

        // Symbol Properties and its fields grid.
        var symbol = screen.Data.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>())
            .First(s => Regex.IsMatch(s.ReferenceField.Text.Text_, "^[A-Za-z]+[0-9]+$"));
        string reference = symbol.ReferenceField.Text.Text_;
        var symbolQuery = new GetItemsById { Header = header.Clone() }; symbolQuery.Items.Add(symbol.Id.Clone());
        async Task<string> Reference() => (await Settled(t => client.InvokeAsync<GetItemsById, GetItemsResponse>(symbolQuery, t)))
            .Items.Single().Unpack<SchematicSymbolInstance>().ReferenceField.Text.Text_;

        await OpenProperties(symbol.Id, symbolDialog, "properties-cancel");
        await Cancel(symbolDialog, "properties-cancel");
        await Unchanged(clean, "Cancelling Symbol Properties");

        await OpenProperties(symbol.Id, symbolDialog, "properties-unchanged");
        Key("Return", symbolDialog, control: true);
        await Window(symbolDialog, false, "properties-unchanged");
        await Unchanged(clean, "Accepting unchanged Symbol Properties");
        Assert.AreEqual(reference, await Reference());

        await OpenProperties(symbol.Id, symbolDialog, "properties-edit");
        await AppendToFirstValue(symbolDialog, "properties-edited");
        var edited = await Advanced(clean);
        Assert.AreEqual(reference + "7", await Reference(), "The fields grid edit must reach the placed symbol.");
        await OneChange(clean, "Edit Symbol Properties", SchematicChange.Types.Kind.Commit);
        Assert.AreNotEqual(clean.StateSha256, edited.StateSha256);
        Assert.IsTrue(edited.NativeContentDirty);
        await Undo(edited, clean, "Symbol Properties");
        Assert.AreEqual(reference, await Reference());

        // Symbol Properties on a symbol that a duplication or a move still carries, with automatic
        // field placement on: the carrying tool owns the edit, so cancelling it must leave nothing,
        // not even an undo entry.  Placing from the symbol chooser needs a symbol library the fixture
        // does not have; a duplicated symbol is placed by the same kind of carrying tool.  The probe's
        // fields are first marked as automatically placed but left away from their automatic places,
        // so placing them after the dialog really moves them.
        var carried = (await Settled(t => client.InvokeAsync<GetItemsById, GetItemsResponse>(symbolQuery, t)))
            .Items.Single().Unpack<SchematicSymbolInstance>();
        carried.FieldsAutoplaced = true;
        carried.ReferenceField.Text.Position.XNm += 5080000;
        carried.ValueField.Text.Position.XNm += 5080000;
        var autoplaced = new ApplySchematicItemBatch
        {
            Document = document.Clone(), Description = "Mark the probe's fields as automatically placed"
        };
        autoplaced.Operations.Add(new SchematicItemOperation { Update = Any.Pack(carried) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(autoplaced, token);
        var placing = await Saved();
        Assert.AreNotEqual(clean.StateSha256, placing.StateSha256);
        async Task<SchematicScreenData> ScreenData() =>
            (await Settled(t => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document.Clone() }, t))).Data;
        var placingData = await ScreenData();
        async Task SameScreen(string step)
        {
            // The sheet's items, cached definitions and settings, the reference inventory of handed
            // out designators included.  A difference is kept as evidence before it fails.
            var after = await ScreenData();
            if (!placingData.Equals(after))
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-owner-{step}-expected.json"),
                    SchematicJson.Formatter.Format(placingData), token);
                await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-owner-{step}-actual.json"),
                    SchematicJson.Formatter.Format(after), token);
            }
            CollectionAssert.AreEqual(placingData.Metadata?.ReferenceInventory?.Allocated.ToList() ?? new List<string>(),
                after.Metadata?.ReferenceInventory?.Allocated.ToList() ?? new List<string>(),
                $"{step} must return every designator it handed out.");
            Assert.AreEqual(placingData, after, $"{step} must leave the sheet exactly as it was (both states are kept as evidence).");
        }
        async Task EditWhileCarried(string carry, bool control, string step)
        {
            await Select(symbol.Id);
            Key(carry, "Schematic Editor", control);
            // The carried symbol now follows the pointer.
            await Task.Delay(500, token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}-carried.png"), token);
            Key("e", "Schematic Editor");
            await Window(symbolDialog, true, step);
            // The dialog opens its first value cell editor through queued events.
            await Task.Delay(500, token);
            await AppendToFirstValue(symbolDialog, step);
            // Hovering gives the canvas keyboard focus without the click that would place the symbol.
            NativeKeyboard.SchematicShortcut(display, processId, "motion", controlKey: false, focusCanvas: true);
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
            await Task.Delay(1000, token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}-cancelled.png"), token);
        }
        await EditWhileCarried("d", true, "duplicate-properties");
        await SameScreen("duplicate-properties");
        await Unchanged(placing, "Cancelling a duplicated symbol after editing its properties");
        await EditWhileCarried("m", false, "move-properties");
        await SameScreen("move-properties");
        await Unchanged(placing, "Cancelling a symbol move after editing its properties");
        Assert.AreEqual(reference, await Reference());
        // Neither cancelled edit left an undo entry: the next undo reverts the field marking above
        // and restores the exact state before it.
        await Undo(placing, clean, "the field marking");

        // Sheet Properties: its commit is pushed or reverted by the tracked owner.
        var cleanSheets = await Saved();
        var sheet = screen.Data.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).First();
        string name = sheet.NameField.Text.Text_;
        var sheetQuery = new GetItemsById { Header = header.Clone() }; sheetQuery.Items.Add(sheet.Id.Clone());
        async Task<string> SheetName() => (await Settled(t => client.InvokeAsync<GetItemsById, GetItemsResponse>(sheetQuery, t)))
            .Items.Single().Unpack<SheetSymbol>().NameField.Text.Text_;

        await OpenProperties(sheet.Id, sheetDialog, "sheet-properties-cancel");
        await Cancel(sheetDialog, "sheet-properties-cancel");
        await Unchanged(cleanSheets, "Cancelling Sheet Properties");

        await OpenProperties(sheet.Id, sheetDialog, "sheet-properties-unchanged");
        Key("Return", sheetDialog, control: true);
        await Window(sheetDialog, false, "sheet-properties-unchanged");
        await Unchanged(cleanSheets, "Accepting unchanged Sheet Properties");
        Assert.AreEqual(name, await SheetName());

        await OpenProperties(sheet.Id, sheetDialog, "sheet-properties-edit");
        await AppendToFirstValue(sheetDialog, "sheet-properties-edited");
        var renamed = await Advanced(cleanSheets);
        Assert.AreEqual(name + "7", await SheetName(), "The sheet name edit must reach the placed sheet.");
        await OneChange(cleanSheets, "Edit Sheet Properties", SchematicChange.Types.Kind.Commit);
        Assert.AreNotEqual(cleanSheets.StateSha256, renamed.StateSha256);
        Assert.IsTrue(renamed.NativeContentDirty);
        await Undo(renamed, cleanSheets, "Sheet Properties");
        Assert.AreEqual(name, await SheetName());

        // Hierarchy pane: new and deleted top-level sheets have no undo entry of their own.
        var beforeSheet = await Saved();
        var hierarchyQuery = new GetSchematicHierarchy { Document = document.Clone() };
        var hierarchy = await Settled(t => client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, t));

        async Task<int> Popups()
        {
            int count = -1;
            NativeKeyboard.SchematicShortcut(display, processId, "", observePopupCount: value => count = value);
            return count;
        }
        async Task OpenPaneMenu(string action, string dialog, int upPresses)
        {
            // Empty space at the bottom of the rendered hierarchy tree; the menu then acts on the
            // selected (current) sheet.  It ends with Expand All and Collapse All.  A closing dialog
            // can swallow the first click, so the menu itself must be seen before any key is sent.
            for (int attempt = 0; ; attempt++)
            {
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(5));
                    while (await Popups() != 0) await Task.Delay(50, idle.Token);
                }
                NativeKeyboard.SchematicShortcut(display, processId, "right-click", controlKey: false, focusCanvas: true,
                    clickFromLeft: 100, clickFromTop: 495);
                var shown = Stopwatch.StartNew();
                while (await Popups() != 1 && shown.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50, token);
                if (await Popups() == 1) break;
                if (attempt == 2) await Failed(action, $"The hierarchy pane menu did not open for {action}.");
            }
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{action}-menu.png"), token);
            Key("End", "Schematic Editor");
            for (int i = 0; i < upPresses; i++) Key("Up", "Schematic Editor");
            Key("Return", "Schematic Editor");
            await Window(dialog, true, action);
        }

        await OpenPaneMenu("sheet-cancel", newSheet, 2);
        Key("Escape", newSheet);
        await Window(newSheet, false, "sheet-cancel");
        await Unchanged(beforeSheet, "Cancelling a new top-level sheet");

        await OpenPaneMenu("sheet-create", newSheet, 2);
        Key("a", newSheet, control: true);
        foreach (char c in sheetName) Key(c == ' ' ? "space" : c.ToString(), newSheet);
        Key("Return", newSheet);
        await Window(newSheet, false, "sheet-create");
        var created = await Advanced(beforeSheet);
        await OneChange(beforeSheet, "New Top-Level Sheet", SchematicChange.Types.Kind.Commit);
        var grown = await Settled(t => client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, t));
        Assert.HasCount(hierarchy.TopLevelSheets.Count + 1, grown.TopLevelSheets);
        var added = grown.TopLevelSheets.Single(s => s.Name == sheetName);
        Assert.AreNotEqual(beforeSheet.StateSha256, created.StateSha256);
        Assert.IsTrue(created.NativeContentDirty);

        // Make the new sheet current so the pane menu acts on it, never on an existing sheet.
        var target = document.Clone(); target.SheetPath = added.Path.Clone();
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = target }, token);
        var current = await State();
        await OpenPaneMenu("sheet-keep", deleteSheet, 3);
        Key("Escape", deleteSheet);
        await Window(deleteSheet, false, "sheet-keep");
        await Unchanged(current, "Declining to delete a top-level sheet");

        await OpenPaneMenu("sheet-delete", deleteSheet, 3);
        Key("Return", deleteSheet);
        await Window(deleteSheet, false, "sheet-delete");
        var removed = await Advanced(current);
        await OneChange(current, "Delete Top-Level Sheet", SchematicChange.Types.Kind.Commit);
        Assert.AreEqual(hierarchy, await Settled(t =>
            client.InvokeAsync<GetSchematicHierarchy, SchematicHierarchyResponse>(hierarchyQuery, t)));
        Assert.AreEqual(beforeSheet.StateSha256, removed.StateSha256,
            "Deleting the new sheet must restore the exact earlier design state.");

        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-restored.png"), token);

        async Task ModalReady(string dialog, string step)
        {
            // Mapping precedes the modal loop; the editor reports busy once the dialog owns it.
            await Window(dialog, true, step);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    try { await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = document.Clone() }, limit.Token); }
                    catch (NativeApiException busy) when (busy.Status == 7) { return; }
                    await Task.Delay(50, limit.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await Failed(step, $"The '{dialog}' dialog never became modal during {step}.");
            }
        }
        async Task Button(string dialog, bool accept, string step)
        {
            // The rendered OK and Cancel buttons of the fixture's 1280x900 dialogs; a synthetic
            // Escape or Return does not reliably reach a focused GTK choice or grid.
            NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, true, accept ? 60 : 150, 25);
            await Window(dialog, false, step);
        }

        // Page Settings: the shared dialog used to mark the design modified, and record a revision
        // and an undo entry, on every OK.
        const string pageDialog = "Page Settings";
        var cleanPage = await Saved();
        NativeKeyboard.SchematicShortcut(display, processId, "F12");
        await ModalReady(pageDialog, "page-cancel");
        await Button(pageDialog, false, "page-cancel");
        await Unchanged(cleanPage, "Cancelling Page Settings");

        // Home selects the first paper size (A5).  Earlier journeys may have left "export to other
        // sheets" checked in this editor's settings; the edit then exports as well, so the
        // unchanged OK that follows has nothing left to export.
        NativeKeyboard.SchematicShortcut(display, processId, "F12");
        await ModalReady(pageDialog, "page-edit");
        Key("Home", pageDialog);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-page-edited.png"), token);
        await Button(pageDialog, true, "page-edit");
        var paged = await Advanced(cleanPage);
        await OneChange(cleanPage, "Edit Page Settings", SchematicChange.Types.Kind.Commit);
        Assert.AreNotEqual(cleanPage.StateSha256, paged.StateSha256);
        Assert.IsTrue(paged.NativeContentDirty);

        NativeKeyboard.SchematicShortcut(display, processId, "F12");
        await ModalReady(pageDialog, "page-unchanged");
        await Button(pageDialog, true, "page-unchanged");
        await Unchanged(paged, "Accepting unchanged Page Settings");
        await Undo(paged, cleanPage, "Page Settings");

        // Schematic Setup: compared with the same saved state as the lifecycle digest.
        const string setupDialog = "Schematic Setup";
        var cleanSetup = await Saved();
        await NativeSetupUi.Open(client, document.Clone(), display, processId, token);
        await Button(setupDialog, false, "setup-cancel");
        await Unchanged(cleanSetup, "Cancelling Schematic Setup");

        await NativeSetupUi.Open(client, document.Clone(), display, processId, token);
        await Button(setupDialog, true, "setup-unchanged");
        await Unchanged(cleanSetup, "Accepting unchanged Schematic Setup");

        var annotation = (await Settled(t => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document.Clone() }, t))).Data.Metadata.Annotation;
        bool chooseY = annotation.Order == SchematicAnnotationOrder.SaoXPosition;
        await NativeSetupUi.Open(client, document.Clone(), display, processId, token);
        // The Annotation page, then its X or Y ordering mnemonic.
        await NativeSetupUi.SelectPage(display, processId, 55, token);
        NativeKeyboard.SchematicShortcut(display, processId, chooseY ? "y" : "x", setupDialog,
            controlKey: false, focusCanvas: false, altKey: true);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-setup-edited.png"), token);
        await Button(setupDialog, true, "setup-edit");
        var setUp = await Advanced(cleanSetup);
        await OneChange(cleanSetup, "Edit Schematic Setup", SchematicChange.Types.Kind.Commit);
        Assert.AreNotEqual(cleanSetup.StateSha256, setUp.StateSha256);
        Assert.IsTrue(setUp.NativeContentDirty);
        Assert.AreEqual(chooseY ? SchematicAnnotationOrder.SaoYPosition : SchematicAnnotationOrder.SaoXPosition,
            (await Settled(t => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document.Clone() }, t))).Data.Metadata.Annotation.Order);
        await Undo(setUp, cleanSetup, "Schematic Setup");

        // Annotate Schematic: its sort order, numbering and first number are saved project
        // settings, applied when the modeless dialog is closed and destroyed.  Opened from its top
        // toolbar button and closed with its rendered Close button (never Annotate).
        const string annotateDialog = "Annotate Schematic";
        async Task OpenAnnotate(string step)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false, focusCanvas: true,
                clickFromLeft: 1005, clickFromTop: 43);
            await Window(annotateDialog, true, step);
            await NativeSetupUi.StableGeometry(display, processId, token, annotateDialog);
        }
        async Task<SchematicAnnotationOrder> AnnotationOrder() =>
            (await Settled(t => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document.Clone() }, t))).Data.Metadata.Annotation.Order;

        var cleanAnnotate = await Saved();
        var originalOrder = await AnnotationOrder();
        await OpenAnnotate("annotate-unchanged");
        await Button(annotateDialog, false, "annotate-unchanged");
        await Unchanged(cleanAnnotate, "Closing unchanged Annotate Schematic");

        bool sortByY = originalOrder == SchematicAnnotationOrder.SaoXPosition;
        await OpenAnnotate("annotate-edit");
        NativeKeyboard.SchematicShortcut(display, processId, sortByY ? "y" : "x", annotateDialog,
            controlKey: false, focusCanvas: false, altKey: true);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-annotate-edited.png"), token);
        await Button(annotateDialog, false, "annotate-edit");
        var annotated = await Advanced(cleanAnnotate);
        await OneChange(cleanAnnotate, "Edit Annotation Settings", SchematicChange.Types.Kind.Commit);
        Assert.AreNotEqual(originalOrder, await AnnotationOrder(), "Closing the dialog must keep the chosen sort order.");
        Assert.AreNotEqual(cleanAnnotate.StateSha256, annotated.StateSha256);
        Assert.IsTrue(annotated.NativeContentDirty);

        // These settings have no undo entry: choosing the original order again is a second edit
        // that returns the exact saved state.
        await OpenAnnotate("annotate-restore");
        NativeKeyboard.SchematicShortcut(display, processId, sortByY ? "x" : "y", annotateDialog,
            controlKey: false, focusCanvas: false, altKey: true);
        await Button(annotateDialog, false, "annotate-restore");
        var reannotated = await Advanced(annotated);
        await OneChange(annotated, "Edit Annotation Settings", SchematicChange.Types.Kind.Commit);
        Assert.AreEqual(originalOrder, await AnnotationOrder());
        Assert.AreEqual(cleanAnnotate.StateSha256, reannotated.StateSha256,
            "Choosing the original order again must return the exact saved state.");

        // Place > Import Sheet.  The first placement brings a child sheet with it.  Cancelling a later
        // placement must leave nothing when its file repeats no identity the design uses, and must
        // record exactly the change it leaves when it does: loading gives whichever duplicate comes
        // later in sheet order a new identity, and for an item on a sheet below the current one that
        // is the existing item, which keeps its new identity after the cancel.  Removing the placed
        // items also removes cached library definitions only they used, so those never stay.
        const string chooser = "Choose Schematic", childNoteText = "Tracked child note";
        string importDirectory = Path.Combine(Path.GetTempPath(), "kicad-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(importDirectory);
        string childFile = Path.Combine(importDirectory, "child.kicad_sch");
        string placedFile = Path.Combine(importDirectory, "placed.kicad_sch");
        string freshFile = Path.Combine(importDirectory, "fresh.kicad_sch");
        string childNote = Guid.NewGuid().ToString("D"), placedRoot = Guid.NewGuid().ToString("D");
        string Opening(string id) =>
            $"(kicad_sch (version 20250114) (generator \"eeschema\") (uuid {id}) (paper \"A4\") (lib_symbols)";
        string Wiring() =>
            $" (wire (pts (xy 20.32 20.32) (xy 40.64 20.32)) (stroke (width 0) (type default)) (uuid {Guid.NewGuid():D}))"
            + $" (text \"Tracked import\" (at 20.32 25.4 0) (effects (font (size 1.27 1.27))) (uuid {Guid.NewGuid():D}))";
        async Task<(int Original, int Notes)> ChildNotes()
        {
            var electrical = await Settled(t => client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(
                new() { Document = document.Clone() }, t));
            var notes = electrical.Hierarchy.Data.Instances.SelectMany(s => s.Items)
                .Where(i => i.Is(SchematicText.Descriptor)).Select(i => i.Unpack<SchematicText>())
                .Where(t => t.Text.Text_ == childNoteText).ToList();
            return (notes.Count(t => t.Id.Value == childNote), notes.Count);
        }
        await File.WriteAllTextAsync(childFile, Opening(Guid.NewGuid().ToString("D"))
            + $" (text \"{childNoteText}\" (at 20.32 30.48 0) (effects (font (size 1.27 1.27))) (uuid {childNote})))", token);
        await File.WriteAllTextAsync(placedFile, Opening(placedRoot) + Wiring()
            + $" (sheet (at 50.8 20.32) (size 20.32 10.16) (stroke (width 0) (type default)) (fill (color 0 0 0 0)) (uuid {Guid.NewGuid():D})"
            + " (property \"Sheetname\" \"Tracked import child\" (at 50.8 19.5 0) (effects (font (size 1.27 1.27)) (justify left bottom)))"
            + " (property \"Sheetfile\" \"child.kicad_sch\" (at 50.8 31.1 0) (effects (font (size 1.27 1.27)) (justify left top)))"
            + $" (instances (project \"import\" (path \"/{placedRoot}\" (page \"2\")))))"
            + " (sheet_instances (path \"/\" (page \"1\"))))", token);
        await File.WriteAllTextAsync(freshFile, Opening(Guid.NewGuid().ToString("D")) + Wiring()
            + " (sheet_instances (path \"/\" (page \"1\"))))", token);
        try
        {
            async Task Import(string step, string file)
            {
                NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: true);
                // Place menu, last item, then up to Import Sheet... past the eleven drawing items.
                NativeKeyboard.SchematicShortcut(display, processId, "p", controlKey: false, altKey: true);
                Key("End", "Schematic Editor");
                for (int i = 0; i < 12; i++) Key("Up", "Schematic Editor");
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}-menu.png"), token);
                Key("Return", "Schematic Editor");
                await Window(chooser, true, step);
                await Task.Delay(500, token);
                // A leading slash opens the chooser's location entry with the path typed so far.
                foreach (char c in file) Key(c == ' ' ? "space" : c.ToString(), chooser);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}-chooser.png"), token);
                Key("Return", chooser);
                await Window(chooser, false, step);
                // The imported items now follow the pointer in the move tool.
                await Task.Delay(1000, token);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-{step}-moving.png"), token);
            }
            async Task CancelImport()
            {
                // Hovering gives the canvas keyboard focus without the click that would place the items.
                NativeKeyboard.SchematicShortcut(display, processId, "motion", controlKey: false, focusCanvas: true);
                NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
                await Task.Delay(1000, token);
            }

            var beforeImport = await Saved();
            await Import("import-place", placedFile);
            // Place it away from the margin point used to give the canvas focus: a later focus
            // click on the placed wire's open end would start a new wire.
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false, focusCanvas: true,
                clickFromRight: 400, clickFromBottom: 250);
            var imported = await Advanced(beforeImport);
            await OneChange(beforeImport, "Import Schematic Sheet Content", SchematicChange.Types.Kind.Commit);
            Assert.AreNotEqual(beforeImport.StateSha256, imported.StateSha256);
            Assert.IsTrue(imported.NativeContentDirty);
            Assert.AreEqual((1, 1), await ChildNotes(), "The placed child sheet must keep its note's identity.");
            // A saved baseline, so the cancelled placements are checked for the modified flag too.
            var placed = await Saved();

            // Precision: nothing in this file is already in the design.
            await Import("import-cancel", freshFile);
            await CancelImport();
            await Unchanged(placed, "Cancelling a sheet import");

            // Recall: the child sheet's own file repeats the identity of the note on the placed child
            // sheet, which follows the current sheet in sheet order and so is the one renumbered.
            await Import("import-repeat", childFile);
            await CancelImport();
            var repeated = await Advanced(placed, "import-repeat");
            await OneChange(placed, "Import Schematic Sheet Content", SchematicChange.Types.Kind.Commit);
            Assert.AreNotEqual(placed.StateSha256, repeated.StateSha256, "The renumbered note must change the saved state.");
            Assert.IsTrue(repeated.NativeContentDirty, "A cancelled placement that left a saved change must mark the design modified.");
            Assert.AreEqual((0, 1), await ChildNotes(),
                "The child sheet must keep its one note, under the new identity the cancelled placement gave it.");
            await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-owner-import-cancel.txt"),
                "Cancelling an import whose file repeats no identity in the design left no saved change, no revision and "
                + "no modified flag." + Environment.NewLine
                + "Cancelling an import of the placed child sheet's own file left the child sheet's note renumbered; that "
                + "saved change was recorded as one 'Import Schematic Sheet Content' revision and marked the design modified."
                + Environment.NewLine, token);

            // Undoing the first placement removes the child sheet with its renumbered note.  Saving then
            // writes exactly the design saved before the import: the project settings list the saved
            // sheets, and that list is only refreshed by a save.
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            await Advanced(repeated);
            await OneChange(repeated, "Undo", SchematicChange.Types.Kind.Undo);
            Assert.AreEqual((0, 0), await ChildNotes());
            var restored = await Saved();
            Assert.AreEqual(beforeImport.StateSha256, restored.StateSha256,
                "Undoing the sheet import and saving must restore the exact saved design state.");
        }
        finally
        {
            Directory.Delete(importDirectory, true);
        }

        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{instanceId}-owner-final.png"), token);
    }

    /// <summary>
    /// Runs the native test binary's measurement of what tracking compares on the largest demo
    /// design (vme-wren): one whole-state capture, the largest single screen, the project
    /// settings with the first top-level sheet, and a staged comparison of the symbol with the
    /// largest library definition.  It runs on its own, after the rendered steps; the host's
    /// load around it is kept with its output as evidence, because the governed host is shared.
    /// The staged comparison must stay far below the whole-state capture.
    /// </summary>
    private static async Task MeasureTrackingCostOnLargestDemo(string evidence, string instanceId, CancellationToken token)
    {
        static string Load() => File.Exists("/proc/loadavg")
            ? string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3))
            : "unavailable";
        string loadBefore = Load();
        string root = FindRoot();
        string binary = Path.Combine(root, "automation", "artifacts", "native", "qa", "tests", "eeschema", "qa_symbol_graphic_identity");
        Assert.IsTrue(File.Exists(binary), "The native tracking test binary must be built first.");
        string scratch = Directory.CreateTempSubdirectory("kicad-tracking-cost-").FullName;
        try
        {
            var start = new ProcessStartInfo(binary)
            {
                WorkingDirectory = Path.GetDirectoryName(binary)!, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "--run_test=SchTrackedChange/MeasuresTrackingCostOnTheLargestDemo", "--log_level=message" })
                start.ArgumentList.Add(arg);
            start.Environment["KICAD_SOURCE_DIR"] = root;
            // The measurement copies the demo into its temporary directory; keep that inside this
            // scratch directory so it is removed even if the process has to be stopped.
            start.Environment["TMPDIR"] = scratch;
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(scratch, "cache");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(420));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            }
            string text = await output + await error;
            string conditions = $"Measured after the rendered journey, not alongside it, on a shared host with "
                + $"{Environment.ProcessorCount} processors (Debug build). Load average (1, 5, 15 min) before: {loadBefore}; "
                + $"after: {Load()}." + Environment.NewLine;
            await File.WriteAllTextAsync(Path.Combine(evidence, $"{instanceId}-tracking-cost-vme-wren.txt"), conditions + text,
                CancellationToken.None);
            Assert.AreEqual(0, process.ExitCode, "The large-design tracking measurement failed:\n" + text);
            double Median(string measurement)
            {
                var match = Regex.Match(text, "tracking-cost " + measurement + @" median=([0-9.]+)");
                Assert.IsTrue(match.Success, $"The measurement has no {measurement} line:\n" + text);
                return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            double whole = Median("whole_state_capture_ms"), staged = Median("staged_symbol_compare_ms");
            double settingsAndFirst = Median("settings_and_first_sheet_capture_ms");
            Median("largest_screen_capture_ms");
            Console.WriteLine($"Native tracking cost on vme-wren: whole-state capture {whole:F1} ms, project settings and "
                + $"first sheet {settingsAndFirst:F1} ms, staged symbol comparison {staged:F1} ms.");
            Assert.IsLessThan(whole / 10, staged, "A staged comparison must stay far below a whole-state capture.");
            Assert.IsLessThan(whole, settingsAndFirst, "The project settings and first sheet must cost less than the whole design.");
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }
}
