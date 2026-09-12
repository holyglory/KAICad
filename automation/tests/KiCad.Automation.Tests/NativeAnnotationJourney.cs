using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyAnnotation(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot state, SchematicAnnotationSettings policy)
        {
            var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = state.Revision.Clone(),
                DocumentEpoch = state.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Annotation policy XML round trip" };
            batch.Operations.Add(new SchematicItemOperation { SetAnnotation = policy.Clone() });
            return batch;
        }
        // Normalize only through a real native save/reload before establishing
        // the exact baseline; no format-version fields are dropped from tests.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var before = await Read();
        Assert.IsNotNull(before.Data.Metadata.Annotation);
        var desired = before.Data.Metadata.Annotation.Clone();
        desired.StartAfter = 317; desired.Order = SchematicAnnotationOrder.SaoYPosition;
        desired.Method = SchematicAnnotationMethod.SamSheetTimes1000;
        desired.ReuseDesignators = !desired.ReuseDesignators;
        foreach (var invalid in new[] { 0, 3, -1 })
        {
            var malformed = desired.Clone(); malformed.Order = (SchematicAnnotationOrder)invalid;
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(before, malformed)));
            Assert.AreEqual(before, await Read());
        }
        var unknown = SchematicAnnotationSettings.Parser.ParseFrom(desired.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(before, unknown)));
        var failed = Batch(before, desired);
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(before, await Read(), "Later operation failure must roll back the policy without a revision.");
        var xml = before.Data.Clone(); xml.Metadata.Annotation = desired;
        var roundTrip = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(xml));
        var batch = Batch(before, desired); batch.Operations.Clear();
        batch.Operations.Add(SchematicItemDelta.Plan(before.Data, roundTrip));
        var result = await Apply(batch);
        Assert.IsTrue(result.AnnotationChanged);
        var changed = await Read();
        Assert.AreEqual(roundTrip, changed.Data);
        Assert.AreEqual(before.Revision.Sequence + 1, changed.Revision.Sequence);
        Assert.AreEqual(result, await Apply(batch));
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.IsFalse((await Apply(Batch(changed, desired))).AnnotationChanged);
        Assert.AreEqual(changed, await Read());
        foreach (var (key, expected) in new[] { ("z", before.Data), ("y", changed.Data) })
        {
            var previous = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot actual;
            do
            {
                actual = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, limit.Token);
                if (actual.Revision.Equals(previous.Revision)) await Task.Delay(50, limit.Token);
            } while (actual.Revision.Equals(previous.Revision));
            Assert.AreEqual(expected, actual.Data);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read();
        Assert.AreEqual(changed.Data, reopened.Data);
        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        Assert.AreEqual(reopened, observation.Snapshot);
        Assert.AreEqual(reopened.Revision, observation.Preview.Revision);
        await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-annotation-policy.png"), observation.Preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, processId + "-annotation-policy.xml"), SchematicDataXml.Write(reopened.Data), token);
        await Apply(Batch(reopened, before.Data.Metadata.Annotation));
        Assert.AreEqual(before.Data, (await Read()).Data);

        // Exercise a human edit through the actual Annotation page, using its
        // native X/Y mnemonic, and carry it across a page switch before OK.
        foreach (bool accept in new[] { false, true })
        {
            var baseline = await Read();
            var edited = baseline.Data.Clone();
            bool chooseY = baseline.Data.Metadata.Annotation.Order == SchematicAnnotationOrder.SaoXPosition;
            edited.Metadata.Annotation.Order = chooseY ? SchematicAnnotationOrder.SaoYPosition : SchematicAnnotationOrder.SaoXPosition;
            await NativeSetupUi.Open(client, document, display, processId, token);
            await NativeSetupUi.SelectPage(display, processId, 55, token);
            NativeKeyboard.SchematicShortcut(display, processId, chooseY ? "y" : "x", "Schematic Setup",
                controlKey: false, focusCanvas: false, altKey: true);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + $"-annotation-manual-{accept}.png"), token);
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup")) await Task.Delay(50, limit.Token);
            var actual = await Read();
            Assert.AreEqual(accept ? edited : baseline.Data, actual.Data);
            Assert.AreEqual(baseline.Revision.Sequence + (accept ? 1UL : 0UL), actual.Revision.Sequence);
            Assert.AreEqual(actual.Data, SchematicDataXml.Read(SchematicDataXml.Write(actual.Data)));
            if (!accept) continue;
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            do
            {
                actual = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, limit.Token);
                if (actual.Revision.Sequence == baseline.Revision.Sequence + 1) await Task.Delay(50, limit.Token);
            } while (actual.Revision.Sequence == baseline.Revision.Sequence + 1);
            Assert.AreEqual(baseline.Data, actual.Data);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        }
    }
}
