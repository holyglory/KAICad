using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyManualFieldMoveRoundTrip(NativeClient client, DocumentSpecifier root,
        DesignRecoveryStore store, string designPath, Guid occurrenceId, string evidence, string instanceId,
        int processId, string display, CancellationToken token)
    {
        var saved = store.Read()!; Guid nativeId = saved.State.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrenceId).NativeObjectId;
        string originalEngineering = EngineeringDesignXml.Write(saved.State.Baseline.Engineering, saved.State.KnowledgeLibraries);
        var header = new ItemHeader { Document = root.Clone() };
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
        var before = await Capture(token); var original = Symbol(before.Electrical.Hierarchy.Data);
        Assert.AreNotEqual(LockedState.LsLocked, original.Locked);
        await using var automatic = await AutomaticDesignSynchronization.StartAsync(store, client, designPath, saved.RevisionToken, token);
        await Synchronized(before);
        try
        {
            await BeginMove();
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
            var cancelled = await Ready(before.State.Revision.Sequence, changed: false);
            Assert.AreEqual(before.State.Revision, cancelled.State.Revision);
            Assert.AreEqual(original, Symbol(cancelled.Electrical.Hierarchy.Data));
            Assert.IsFalse(cancelled.State.NativeContentDirty);

            await BeginMove();
            var query = new GetItemsById { Header = header.Clone() }; query.Items.Add(new KIID { Value = nativeId.ToString("D") });
            int x = 620, y = 460;
            using (var motion = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                motion.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    NativeKeyboard.SchematicShortcut(display, processId, "motion", controlKey: false, clickFromLeft: x, clickFromTop: y);
                    await Task.Delay(100, motion.Token);
                    var live = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, motion.Token)).Items.Single().Unpack<SchematicSymbolInstance>();
                    if (!live.ReferenceField.Text.Position.Equals(original.ReferenceField.Text.Position)) break;
                    x = x == 640 ? 620 : x + 10; y = y == 480 ? 460 : y + 10;
                }
            }
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false, clickFromLeft: x, clickFromTop: y);
            var moved = await Ready(before.State.Revision.Sequence, changed: true);
            var movedSymbol = Symbol(moved.Electrical.Hierarchy.Data);
            Assert.AreNotEqual(original.ReferenceField.Text.Position, movedSymbol.ReferenceField.Text.Position);
            Assert.AreEqual(original.Position, movedSymbol.Position); Assert.AreEqual(original.Transform, movedSymbol.Transform);
            Assert.AreEqual(original.Definition, movedSymbol.Definition); Assert.AreEqual(original.ValueField, movedSymbol.ValueField);
            Assert.AreEqual(original.ReferenceField.Text.Text_, movedSymbol.ReferenceField.Text.Text_);
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header.Clone() }, token);
            await Synchronized(moved);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-field-xml.xml"), await File.ReadAllTextAsync(designPath, token), token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-manual-field-window.png"), token);

            await FocusedSchematicShortcut(client, root, processId, display, "z", token);
            var undone = await Ready(moved.State.Revision.Sequence, changed: true);
            Assert.AreEqual(original.ReferenceField.Text.Position, Symbol(undone.Electrical.Hierarchy.Data).ReferenceField.Text.Position);
            await Synchronized(undone);
            await FocusedSchematicShortcut(client, root, processId, display, "y", token);
            var redone = await Ready(undone.State.Revision.Sequence, changed: true);
            Assert.AreEqual(movedSymbol.ReferenceField.Text.Position, Symbol(redone.Electrical.Hierarchy.Data).ReferenceField.Text.Position);
            await Synchronized(redone);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-field-proof.json"), JsonSerializer.Serialize(new
            { instanceId, occurrenceId, nativeId, actualKeyboardAndPointerMove = true, cancellationPreservedDesign = true,
                parentPlacementPreserved = true, fieldTextPreserved = true, automaticReverseXml = true,
                nativeUndoRedoReverseXml = true, connectivityPreserved = true, crossPlatformReady = false }), token);
        }
        catch
        {
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-manual-field-failed.png"), CancellationToken.None);
            throw;
        }
        finally
        {
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header.Clone() }, token);
        }

        async Task BeginMove()
        {
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header.Clone() }, token);
            var facts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(new() { Document = root.Clone() }, token);
            var reference = facts.Objects.Single(o => o.OwnerId?.Value == nativeId.ToString("D")
                && o.Kind == SchematicPresentationObject.Types.Kind.ReferenceDesignator);
            Assert.IsTrue(reference.Visible);
            // Runtime field IDs are valid only for this observed editor session;
            // persistent XML identity remains owner plus exact field slot/name.
            var select = new AddToSelection { Header = header.Clone() }; select.Items.Add(reference.Id.Clone());
            await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
            NativeKeyboard.SchematicShortcut(display, processId, "m", controlKey: false, focusCanvas: false);
            using var begin = CancellationTokenSource.CreateLinkedTokenSource(token); begin.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { await Capture(begin.Token); }
                catch (NativeApiException error) when (error.Status == 7) { break; }
                await Task.Delay(100, begin.Token);
            }
        }
        Task<CheckedSchematicState> Capture(CancellationToken cancellation) => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, cancellation);
        SchematicSymbolInstance Symbol(SchematicHierarchyData data) => data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == nativeId.ToString("D"));
        async Task<CheckedSchematicState> Ready(ulong prior, bool changed)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { var state = await Capture(wait.Token); if (!changed || state.State.Revision.Sequence > prior) return state; }
                catch (NativeApiException error) when (error.Status == 7) { }
                await Task.Delay(100, wait.Token);
            }
        }
        async Task Synchronized(CheckedSchematicState expected)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(20));
            var status = automatic.Inspect();
            while (status.Phase != AutomaticDesignPhase.Watching || store.Read()!.State.NativeRevision.Sequence < expected.State.Revision.Sequence
                || !Symbol(store.Read()!.State.Baseline.Schematic).ReferenceField.Equals(Symbol(expected.Electrical.Hierarchy.Data).ReferenceField))
            {
                Assert.AreNotEqual(AutomaticDesignPhase.Paused, status.Phase, status.ErrorMessage);
                status = await automatic.WaitAsync(status.Sequence, wait.Token);
            }
            var actual = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, wait.Token), saved.State.KnowledgeLibraries);
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(expected.Electrical.Hierarchy.Data, actual.Schematic));
            Assert.AreEqual(originalEngineering, EngineeringDesignXml.Write(actual.Engineering, saved.State.KnowledgeLibraries));
            var comparison = SchematicElectricalComparison.Compare(actual, expected.Electrical, saved.State.KnowledgeLibraries);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent);
            Assert.AreEqual(expected.State.Revision, (await Capture(wait.Token)).State.Revision,
                "Reverse publication must not apply an extra native edit.");
        }
    }
}
