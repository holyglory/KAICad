using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeItemDelta(NativeClient client, DocumentSpecifier document,
        string textId, int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        async Task<SchematicObservation> Observe(SchematicScreenDataSnapshot expected, string phase)
        {
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token);
            Assert.AreEqual(expected, observation.Snapshot, "Image-associated data must describe the current edit state.");
            Assert.AreEqual(expected.Revision, observation.Preview.Revision);
            Assert.AreEqual(document, observation.Preview.Document);
            Assert.IsFalse(observation.Snapshot.TrackingComplete);
            Assert.IsFalse(observation.Preview.TrackingComplete);
            Assert.IsTrue(observation.Preview.WidthPixels > 0 && observation.Preview.HeightPixels > 0);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-observation-" + phase + ".png"),
                observation.Preview.Png.ToByteArray(), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-observation-" + phase + ".xml"),
                SchematicDataXml.Write(observation.Snapshot.Data), token);
            return observation;
        }
        var baseline = await Read();
        var desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(baseline.Data));
        var original = desired.Items.Single(i => i.Is(SchematicText.Descriptor) && i.Unpack<SchematicText>().Id.Value == textId);
        var moved = original.Unpack<SchematicText>(); moved.Text.Position.XNm += 2000000;
        desired.Items[desired.Items.IndexOf(original)] = Any.Pack(moved);
        var added = moved.Clone(); added.Id.Value = Guid.NewGuid().ToString("D");
        // Keep notes separate: exact-image restoration tests document state,
        // not antialias compositing order between overlapping glyphs.
        added.Text.Text_ = "XML-backed instruction"; added.Text.Position.YNm += 15000000;
        desired.Items.Add(Any.Pack(added));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-item-delta-desired.xml"), SchematicDataXml.Write(desired), token);
        var operations = SchematicItemDelta.Plan(baseline.Data, desired);
        Assert.AreEqual(2, operations.Count);
        var otherOriginal = baseline.Data.Items.First(i => i.Is(SchematicText.Descriptor) && i.Unpack<SchematicText>().Id.Value != textId);
        var nativeEdit = otherOriginal.Unpack<SchematicText>(); nativeEdit.Text.Text_ = "Independent native note";
        var nativeBatch = new ApplySchematicItemBatch { Document = document, Description = "Independent native edit" };
        nativeBatch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(nativeEdit) });
        var nativeWording = original.Unpack<SchematicText>(); nativeWording.Text.Text_ = "Native wording";
        nativeBatch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(nativeWording) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(nativeBatch, token);
        var nativeBefore = await Read();
        var beforeObservation = await Observe(nativeBefore, "before");
        var conflictingXml = desired.Clone();
        var competing = nativeEdit.Clone(); competing.Text.Text_ = "Competing XML note";
        conflictingXml.Items[conflictingXml.Items.IndexOf(otherOriginal)] = Any.Pack(competing);
        var conflict = SchematicItemMerge.Plan(baseline.Data, conflictingXml, nativeBefore.Data);
        Assert.IsFalse(conflict.CanApply); Assert.IsNull(conflict.Merged); Assert.AreEqual(0, conflict.NativeOperations.Count);
        var versions = conflict.Conflicts.Single();
        Assert.AreEqual(otherOriginal, versions.Baseline);
        Assert.AreEqual(Any.Pack(competing), versions.Xml); Assert.AreEqual(Any.Pack(nativeEdit), versions.Native);
        Assert.AreEqual(nativeBefore, await Read(), "Conflict planning must not alter native state.");
        string checkpointPath = Path.Combine(evidence, instanceId + "-sync-checkpoint.json");
        var store = new SchematicSyncStore(checkpointPath);
        var checkpoint = store.Save(new(Guid.NewGuid(),
            new KiCad.Automation.Model.DocumentRevision(nativeBefore.Revision.Epoch, nativeBefore.Revision.Sequence),
            nativeBefore.TrackingComplete, baseline.Data, conflictingXml, nativeBefore.Data), null);
        store = new SchematicSyncStore(checkpointPath);
        var recovered = store.Read(); Assert.IsNotNull(recovered);
        var recoveredConflict = SchematicItemMerge.Plan(recovered.State.Baseline, recovered.State.Xml, recovered.State.Native);
        Assert.AreEqual(versions, recoveredConflict.Conflicts.Single());
        Assert.AreEqual(checkpoint.RevisionToken, recovered.RevisionToken);
        // Explicitly retain the native note; reopening alone never resolves it.
        checkpoint = store.Resolve(recovered.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice> { [versions.ObjectId!.Value] = SchematicConflictChoice.Native });
        store = new SchematicSyncStore(checkpointPath);
        checkpoint = store.Read()!;
        Assert.AreEqual(conflictingXml, checkpoint.State.Xml, "Choosing must retain the original conflicting XML.");
        var merged = SchematicSyncStore.Plan(checkpoint.State);
        Assert.IsTrue(merged.CanApply); Assert.IsNotNull(merged.Merged);
        desired = merged.Merged;
        Assert.IsTrue(desired.Items.Contains(Any.Pack(nativeEdit)), "The reverse XML update must preserve the native edit.");
        Assert.AreEqual(desired, SchematicDataXml.Read(SchematicDataXml.Write(desired)));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-item-merged.xml"), SchematicDataXml.Write(desired), token);
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Apply XML item delta",
            DocumentEpoch = nativeBefore.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), ExpectedRevision = nativeBefore.Revision };
        batch.Operations.Add(merged.NativeOperations);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var changed = await Read();
        Assert.AreEqual(0, SchematicItemDelta.Plan(changed.Data, desired).Count,
            "Unchanged synchronization must not generate more edits.");
        Assert.AreEqual(baseline.Data.Items.Count + 1, changed.Data.Items.Count);
        var combinedNote = changed.Data.Items.Single(i => i.Is(SchematicText.Descriptor)
            && i.Unpack<SchematicText>().Id.Value == textId).Unpack<SchematicText>();
        Assert.AreEqual(nativeWording.Text.Text_, combinedNote.Text.Text_);
        Assert.AreEqual(moved.Text.Position, combinedNote.Text.Position,
            "Native wording and the model's placement must both survive on the same note.");
        foreach (var unchanged in nativeBefore.Data.Items.Where(i => !i.Equals(Any.Pack(nativeWording))))
            Assert.IsTrue(changed.Data.Items.Contains(unchanged), "Unrelated native content must remain unchanged.");
        checkpoint = store.Save(checkpoint.State with
        {
            NativeRevision = new(changed.Revision.Epoch, changed.Revision.Sequence),
            Baseline = changed.Data, Xml = desired, Native = changed.Data, Resolution = null
        }, checkpoint.RevisionToken);
        Assert.AreEqual(0, SchematicItemMerge.Plan(checkpoint.State.Baseline, checkpoint.State.Xml, checkpoint.State.Native).NativeOperations.Count);
        var changedObservation = await Observe(changed, "changed");
        Assert.AreEqual(beforeObservation.Preview.Viewport, changedObservation.Preview.Viewport);
        Assert.IsFalse(beforeObservation.Preview.Png.Equals(changedObservation.Preview.Png),
            "Visible note movement/addition must produce a new rendered image, not stale pixels.");
        var preview = changedObservation.Preview;
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-item-delta.png"), preview.Png.ToByteArray(), token);
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        SchematicScreenDataSnapshot undone;
        do
        {
            undone = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
            if (undone.Revision.Equals(changed.Revision)) await Task.Delay(100, deadline.Token);
        } while (undone.Revision.Equals(changed.Revision));
        Assert.AreEqual(nativeBefore.Data, undone.Data, "Undo the XML update without undoing the independent native edit.");
        Assert.AreEqual(0, SchematicItemDelta.Plan(undone.Data, nativeBefore.Data).Count);
        var undoObservation = await Observe(undone, "undo");
        Assert.AreEqual(beforeObservation.Preview.Viewport, undoObservation.Preview.Viewport);
        Assert.IsTrue(beforeObservation.Preview.Png.Equals(undoObservation.Preview.Png),
            "Undo must restore the image as well as the stored objects.");
        var reverseXml = SchematicItemMerge.Plan(checkpoint.State.Baseline, checkpoint.State.Xml, undone.Data);
        Assert.IsTrue(reverseXml.CanApply); Assert.AreEqual(0, reverseXml.NativeOperations.Count);
        if (!undone.Data.Equals(reverseXml.Merged))
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-reverse-undo-native.json"),
                SchematicJson.Formatter.Format(undone.Data), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-reverse-undo-merged.json"),
                SchematicJson.Formatter.Format(reverseXml.Merged!), token);
            Assert.Fail("Native undo must be available for reverse XML synchronization.\n"
                + NativeSnapshotDifference.Describe(undone.Data, reverseXml.Merged!));
        }
        NativeKeyboard.SchematicShortcut(display, processId, "y");
        using var redoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        redoDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        SchematicScreenDataSnapshot redone;
        do
        {
            redone = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, redoDeadline.Token);
            if (redone.Revision.Equals(undone.Revision)) await Task.Delay(100, redoDeadline.Token);
        } while (redone.Revision.Equals(undone.Revision));
        Assert.AreEqual(changed.Data, redone.Data);
        var redoObservation = await Observe(redone, "redo");
        Assert.IsTrue(changedObservation.Preview.Png.Equals(redoObservation.Preview.Png),
            "Redo must restore the edited image alongside its data.");
        var reverse = SchematicItemDelta.Plan(redone.Data, baseline.Data);
        Assert.AreEqual(3, reverse.Count);
        Assert.AreEqual(added.Id, reverse.Single(o => o.OperationCase == SchematicItemOperation.OperationOneofCase.Remove).Remove);
        var restore = new ApplySchematicItemBatch { Document = document, Description = "Restore XML item state",
            DocumentEpoch = redone.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), ExpectedRevision = redone.Revision };
        restore.Operations.Add(reverse);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(restore, token);
        Assert.AreEqual(baseline.Data, (await Read()).Data);
    }
}
