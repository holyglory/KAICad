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
            await VerifyDirectOwnerTracking(client, document, processId, display, evidence, instanceId, token);
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
    /// Drives the rendered Symbol Properties dialog with its fields grid, the Sheet Properties
    /// dialog and the hierarchy pane's top-level sheet actions, and compares the exact persisted
    /// state digest, journal revision and modified flag after each action.  It also measures one
    /// whole-state capture, the comparison every tracked native owner makes before and after.
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
        async Task<DocumentLifecycleState> Advanced(DocumentLifecycleState since)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var current = await State();
                if (current.Revision.Sequence != since.Revision.Sequence) return current;
                await Task.Delay(50, limit.Token);
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

        // The cost of one whole-state capture: the lifecycle digest is built from exactly the
        // groups a tracked owner compares, so each read performs one such capture.
        var samples = new List<double>();
        for (int sample = 0; sample < 7; sample++)
        {
            var timer = Stopwatch.StartNew();
            var observed = await State();
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.AreEqual(clean.StateSha256, observed.StateSha256, "Reading the state must not change it.");
        }
        samples.Sort();
        string cost = $"Whole-state capture over {clean.NativeFiles.Count - 1} screen file(s) and the project settings: "
            + $"median {samples[samples.Count / 2]:F1} ms, min {samples[0]:F1} ms, max {samples[^1]:F1} ms per lifecycle read "
            + "(Debug build, including the request round trip). Tracked owners capture twice per action.";
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
    }
}
