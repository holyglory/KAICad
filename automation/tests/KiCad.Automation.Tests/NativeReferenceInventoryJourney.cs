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
    private static async Task VerifyReferenceInventory(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch value) => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(value, token);
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot state, SchematicReferenceInventory inventory)
        {
            var value = new ApplySchematicItemBatch { Document = document, ExpectedRevision = state.Revision.Clone(),
                DocumentEpoch = state.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Reference inventory XML round trip" };
            value.Operations.Add(new SchematicItemOperation { SetReferenceInventory = inventory.Clone() });
            return value;
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var before = await Read();
        Assert.IsNotNull(before.Data.Metadata.ReferenceInventory);
        var expected = before.Data.Clone();
        var entries = before.Data.Metadata.ReferenceInventory.Allocated.Concat(new[] { "DELETED123", "X,1", "R2147483646", "R2147483647" })
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        expected.Metadata.ReferenceInventory.Allocated.Clear(); expected.Metadata.ReferenceInventory.Allocated.Add(entries);
        foreach (string[] invalid in new[] { new[] { "R1", "R1" }, new[] { "R001" }, new[] { "R0" }, new[] { "R9-3" }, new[] { "R2147483648" }, new[] { "R1-2147483647" } })
        {
            var inventory = new SchematicReferenceInventory(); inventory.Allocated.Add(invalid);
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(before, inventory)));
            Assert.AreEqual(before, await Read(), "Invalid allocation history must not change live references, policy or revision.");
        }
        var rejected = Batch(before, expected.Metadata.ReferenceInventory);
        var unknown = SchematicReferenceInventory.Parser.ParseFrom(expected.Metadata.ReferenceInventory.ToByteArray()
            .Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(before, unknown)));
        Assert.AreEqual(before, await Read());
        rejected.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(rejected));
        Assert.AreEqual(before, await Read(), "Later batch failure must restore exact allocation history.");
        var decoded = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(expected));
        var batch = Batch(before, decoded.Metadata.ReferenceInventory);
        batch.Operations.Clear(); batch.Operations.Add(SchematicItemDelta.Plan(before.Data, decoded));
        var result = await Apply(batch);
        Assert.IsTrue(result.ReferenceInventoryChanged);
        var changed = await Read();
        Assert.AreEqual(expected, changed.Data);
        Assert.AreEqual(before.Revision.Sequence + 1, changed.Revision.Sequence);
        Assert.AreEqual(result, await Apply(batch));
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.IsFalse((await Apply(Batch(changed, expected.Metadata.ReferenceInventory))).ReferenceInventoryChanged);
        Assert.AreEqual(changed, await Read());
        foreach (var (key, data) in new[] { ("z", before.Data), ("y", changed.Data) })
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
            Assert.AreEqual(data, actual.Data);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read();
        Assert.AreEqual(expected, reopened.Data);
        await File.WriteAllTextAsync(Path.Combine(evidence, processId + "-reference-inventory.xml"), SchematicDataXml.Write(reopened.Data), token);
        // Exercise the adjacent policy update with nonempty historical records.
        // The user may permit reuse without deleting the retained inventory.
        var policyChange = Batch(reopened, expected.Metadata.ReferenceInventory);
        policyChange.Operations.Clear();
        var policy = expected.Metadata.Annotation.Clone(); policy.ReuseDesignators = !policy.ReuseDesignators;
        policyChange.Operations.Add(new SchematicItemOperation { SetAnnotation = policy });
        await Apply(policyChange);
        var changedPolicy = await Read();
        var policyExpected = expected.Clone(); policyExpected.Metadata.Annotation = policy;
        Assert.AreEqual(policyExpected, changedPolicy.Data);
        var restorePolicy = Batch(changedPolicy, expected.Metadata.ReferenceInventory);
        restorePolicy.Operations.Clear();
        restorePolicy.Operations.Add(new SchematicItemOperation { SetAnnotation = expected.Metadata.Annotation.Clone() });
        await Apply(restorePolicy);
        reopened = await Read();
        Assert.AreEqual(expected, reopened.Data);
        // Removing inventory records is a distinct explicit operation, not a
        // side effect of deleting a schematic symbol or changing reuse policy.
        var empty = new SchematicReferenceInventory();
        Assert.IsTrue((await Apply(Batch(reopened, empty))).ReferenceInventoryChanged);
        var cleared = await Read();
        var clearedExpected = expected.Clone(); clearedExpected.Metadata.ReferenceInventory = empty;
        Assert.AreEqual(clearedExpected, cleared.Data);
        await Apply(Batch(cleared, before.Data.Metadata.ReferenceInventory));
        Assert.AreEqual(before.Data, (await Read()).Data);
    }
}
