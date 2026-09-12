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
    private static async Task VerifySymbolProjectSettings(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot before, SchematicScreenData after)
        {
            var result = new ApplySchematicItemBatch { Document = document, ExpectedRevision = before.Revision.Clone(),
                DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"),
                Description = "Project symbol settings XML round trip" };
            result.Operations.Add(SchematicItemDelta.Plan(before.Data, after));
            return result;
        }
        async Task Observe(SchematicScreenDataSnapshot state, string phase)
        {
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
            Assert.AreEqual(state, observation.Snapshot);
            Assert.AreEqual(state.Revision, observation.Preview.Revision);
            await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-symbol-settings-" + phase + ".png"),
                observation.Preview.Png.ToByteArray(), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, processId + "-symbol-settings-" + phase + ".xml"),
                SchematicDataXml.Write(state.Data), token);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var before = await Read();
        Assert.IsNotNull(before.Data.Metadata.FieldTemplates);
        Assert.IsNotNull(before.Data.Metadata.SymbolComparison);
        var desired = before.Data.Clone();
        desired.Metadata.FieldTemplates = SchematicSymbolProjectSettingsTests.Templates();
        foreach (var field in SchematicSymbolComparisonSettings.Descriptor.Fields.InFieldNumberOrder())
            field.Accessor.SetValue(desired.Metadata.SymbolComparison, !(bool)field.Accessor.GetValue(desired.Metadata.SymbolComparison));
        var decoded = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = Batch(before, decoded);
        Assert.HasCount(2, batch.Operations);
        foreach (var name in new[] { "", "REFERENCE", "bad\0name", "Documentation" })
        {
            var malformed = batch.Clone(); malformed.OperationId = Guid.NewGuid().ToString("D");
            malformed.Operations.Single(o => o.SetFieldTemplates is not null).SetFieldTemplates.Fields.Add(
                new SchematicFieldTemplate { Name = name });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(malformed));
            Assert.AreEqual(before, await Read());
        }
        foreach (bool template in new[] { true, false })
        {
            var malformed = batch.Clone(); malformed.OperationId = Guid.NewGuid().ToString("D");
            if (template)
            {
                var value = malformed.Operations.Single(o => o.SetFieldTemplates is not null).SetFieldTemplates;
                value.Fields[0] = SchematicFieldTemplate.Parser.ParseFrom(value.Fields[0].ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
            }
            else
            {
                var operation = malformed.Operations.Single(o => o.SetSymbolComparison is not null);
                operation.SetSymbolComparison = SchematicSymbolComparisonSettings.Parser.ParseFrom(
                    operation.SetSymbolComparison.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
            }
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(malformed));
            Assert.AreEqual(before, await Read(), "Rejected policy must roll back preceding template edits too.");
        }
        var failed = batch.Clone(); failed.OperationId = Guid.NewGuid().ToString("D");
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(before, await Read());
        var result = await Apply(batch);
        Assert.IsTrue(result.FieldTemplatesChanged && result.SymbolComparisonChanged);
        var applied = await Read();
        Assert.AreEqual(before.Revision.Sequence + 1, applied.Revision.Sequence);
        Assert.AreEqual(desired, applied.Data);
        Assert.AreEqual(result, await Apply(batch));
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        var noop = Batch(applied, desired);
        // Test native unchanged replacement, not just an empty planner result.
        noop.Operations.Add(new SchematicItemOperation { SetFieldTemplates = desired.Metadata.FieldTemplates.Clone() });
        noop.Operations.Add(new SchematicItemOperation { SetSymbolComparison = desired.Metadata.SymbolComparison.Clone() });
        var unchanged = await Apply(noop);
        Assert.IsFalse(unchanged.FieldTemplatesChanged || unchanged.SymbolComparisonChanged);
        Assert.AreEqual(applied, await Read());
        await Observe(applied, "applied");
        foreach (var (key, expected) in new[] { ("z", before.Data), ("y", desired) })
        {
            var prior = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot state;
            do
            {
                state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, limit.Token);
                if (state.Revision.Equals(prior.Revision)) await Task.Delay(50, limit.Token);
            } while (state.Revision.Equals(prior.Revision));
            Assert.AreEqual(expected, state.Data);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read();
        Assert.AreEqual(desired, reopened.Data);
        await Observe(reopened, "reopened");
        var empty = desired.Clone(); empty.Metadata.FieldTemplates.Fields.Clear();
        await Apply(Batch(reopened, empty));
        Assert.AreEqual(empty, (await Read()).Data);
        await Apply(Batch(await Read(), before.Data));
        Assert.AreEqual(before.Data, (await Read()).Data);
    }
}
