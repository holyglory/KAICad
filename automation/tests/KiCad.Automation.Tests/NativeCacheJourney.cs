using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeCacheTransaction(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read(CancellationToken? cancellation = null) =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, cancellation ?? token);
        async Task Observe(SchematicScreenDataSnapshot expected, string phase)
        {
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token);
            Assert.AreEqual(expected, observation.Snapshot);
            Assert.AreEqual(expected.Revision, observation.Preview.Revision);
            Assert.IsTrue(observation.Preview.WidthPixels > 0 && observation.Preview.HeightPixels > 0);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-cache-" + phase + ".png"),
                observation.Preview.Png.ToByteArray(), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-cache-" + phase + ".xml"),
                SchematicDataXml.Write(observation.Snapshot.Data), token);
        }
        var originalFixture = await Read();
        SchematicItemOperation Cache(SchematicScreenData data)
        {
            var state = new SchematicLibraryCacheState { ScreenId = data.Metadata.ScreenId.Clone() };
            state.Definitions.Add(data.CachedSymbols.Select(c => c.Clone()));
            return new() { ReplaceLibraryCache = state };
        }
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot before, SchematicScreenData data)
        {
            var batch = new ApplySchematicItemBatch { Document = document,
                ExpectedRevision = before.Revision.Clone(), DocumentEpoch = before.Revision.Epoch,
                OperationId = Guid.NewGuid().ToString("D"), Description = "Restore complete symbol cache" };
            batch.Operations.Add(Cache(data));
            return batch;
        }
        async Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);

        // Build the intended local-definition fixture explicitly. Previous
        // revisions accidentally depended on a placement-only decode bug to
        // allocate a cache alias before this journey began.
        var fixtureSymbol = originalFixture.Data.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).OrderBy(s => s.Id.Value, StringComparer.Ordinal).First();
        string existingKey = fixtureSymbol.LibName.Length != 0 ? fixtureSymbol.LibName
            : (fixtureSymbol.LibraryId ?? fixtureSymbol.Definition.Id).LibraryNickname + ":"
                + (fixtureSymbol.LibraryId ?? fixtureSymbol.Definition.Id).EntryName;
        var localCache = originalFixture.Data.CachedSymbols.Single(c => c.CacheKey == existingKey).Clone();
        localCache.CacheKey = "AutomationCache:LocallyEdited";
        localCache.Definition.Id = new() { LibraryNickname = "AutomationCache", EntryName = "LocallyEdited" };
        localCache.Definition.Keywords += " explicit-local-fixture";
        var localSymbol = fixtureSymbol.Clone();
        localSymbol.LibName = localCache.CacheKey;
        localSymbol.Definition.Id = localCache.Definition.Id.Clone();
        localSymbol.Definition.Keywords = localCache.Definition.Keywords;
        var localData = originalFixture.Data.Clone(); localData.CachedSymbols.Add(localCache);
        var setup = Batch(originalFixture, localData);
        setup.Operations.Insert(0, new SchematicItemOperation { Update = Any.Pack(localSymbol) });
        await Apply(setup);
        var baseline = await Read();

        var noChange = await Apply(Batch(baseline, baseline.Data));
        Assert.IsFalse(noChange.LibraryCacheChanged);
        Assert.AreEqual(baseline, await Read(), "Unchanged cache must not create a revision.");
        var desired = baseline.Data.Clone();
        var unused = desired.CachedSymbols.First().Clone();
        unused.CacheKey = "AutomationCache:Unused";
        unused.Definition.Id.LibraryNickname = "AutomationCache";
        unused.Definition.Id.EntryName = "Unused";
        unused.Definition.DemorganBodyStyles = true;
        unused.Definition.BodyStyle.Clear();
        unused.Definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Standard" });
        unused.Definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Alternate" });
        desired.CachedSymbols.Add(unused);
        // Feed the actual decoded XML to the native transaction, not merely a
        // parallel equality assertion against the original in-memory snapshot.
        var reconstructed = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = Batch(baseline, reconstructed);
        var note = baseline.Data.Items.First(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        note.Text.Text_ += " (cache transaction)";
        batch.Operations.Insert(0, new SchematicItemOperation { Update = Any.Pack(note) });
        var invalid = batch.Clone();
        invalid.OperationId = Guid.NewGuid().ToString("D");
        var originalSymbol = baseline.Data.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>())
            .OrderByDescending(s => s.LibraryId is not null && !s.LibraryId.Equals(s.Definition.Id))
            .ThenBy(s => s.Id.Value, StringComparer.Ordinal).First();
        Assert.IsTrue(originalSymbol.LibraryId is not null && !originalSymbol.LibraryId.Equals(originalSymbol.Definition.Id),
            "The cache fixture must move a locally edited definition with a distinct original library link.");
        var symbol = originalSymbol.Clone();
        symbol.Position.XNm += 2540000;
        batch.Operations.Insert(1, new SchematicItemOperation { Update = Any.Pack(symbol) });
        invalid.Operations[1].ReplaceLibraryCache.Definitions.Add(unused.Clone());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(invalid));
        Assert.AreEqual(baseline, await Read());
        var wrong = batch.Clone(); wrong.OperationId = Guid.NewGuid().ToString("D");
        var wrongStyle = batch.Clone(); wrongStyle.OperationId = Guid.NewGuid().ToString("D");
        wrongStyle.Operations[2].ReplaceLibraryCache.Definitions.Single(c => c.CacheKey == unused.CacheKey)
            .Definition.BodyStyle[1].Name = "Custom";
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(wrongStyle));
        Assert.AreEqual(baseline, await Read(), "Malformed De Morgan state must roll back preceding object edits.");
        wrong.Operations[2].ReplaceLibraryCache.ScreenId.Value = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(wrong));
        Assert.AreEqual(baseline, await Read());
        var lateFailure = batch.Clone(); lateFailure.OperationId = Guid.NewGuid().ToString("D");
        lateFailure.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(lateFailure));
        Assert.AreEqual(baseline, await Read(), "Rejected batch must restore original cache and revision.");

        Assert.IsTrue((await Apply(batch)).LibraryCacheChanged);
        var changed = await Read();
        await Observe(changed, "changed");
        Assert.IsTrue(changed.Data.CachedSymbols.Any(c => c.CacheKey == unused.CacheKey));
        Assert.AreEqual(changed.Data, SchematicDataXml.Read(SchematicDataXml.Write(changed.Data)));
        await Apply(batch);
        Assert.AreEqual(changed, await Read(), "Timeout retry must not add another revision.");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(baseline, baseline.Data)));
        Assert.AreEqual(changed, await Read());
        foreach (var (key, expected) in new[] { ("z", baseline.Data), ("y", changed.Data) })
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot observed;
            do
            {
                observed = await Read(wait.Token);
                if (observed.Revision.Equals(before.Revision)) await Task.Delay(100, wait.Token);
            } while (observed.Revision.Equals(before.Revision));
            Assert.AreEqual(expected, observed.Data, "Native undo/redo must retain unused cache definitions.");
            await Observe(observed, key == "z" ? "undo" : "redo");
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read();
        await Observe(reopened, "reopened");
        Assert.IsTrue(changed.Data.Equals(reopened.Data),
            "Complete cache state must survive native save/reopen: " + NativeSnapshotDifference.Describe(changed.Data, reopened.Data));
        var restore = Batch(reopened, baseline.Data);
        restore.Operations.Insert(0, new SchematicItemOperation {
            Update = Any.Pack(originalSymbol) });
        restore.Operations.Insert(0, new SchematicItemOperation {
            Update = baseline.Data.Items.First(i => i.Is(SchematicText.Descriptor)).Clone() });
        await Apply(restore);
        Assert.AreEqual(baseline.Data, (await Read()).Data);
        var end = await Read();
        var cleanup = Batch(end, originalFixture.Data);
        cleanup.Operations.Insert(0, new SchematicItemOperation { Update = Any.Pack(fixtureSymbol) });
        await Apply(cleanup);
        Assert.AreEqual(originalFixture.Data, (await Read()).Data, "Explicit local fixture setup must not leak into later journeys.");
    }
}
