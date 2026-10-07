using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
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
        var libraryId = fixtureSymbol.LibraryId ?? fixtureSymbol.Definition.Id;
        Assert.IsNotNull(libraryId);
        string symbolLibraryMcpState = Path.Combine(evidence, instanceId + "-symbol-library-mcp");
        await using var symbolLibraryMcp = await StdioMcpFixture.StartAsync(
            SyncHarnessProcessTests.ProductionStartInfo(), symbolLibraryMcpState,
            Path.Combine(evidence, instanceId + "-symbol-library-mcp.stderr.log"), token);
        RequireToolSuccess(await symbolLibraryMcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var symbolLibraryList = await symbolLibraryMcp.Tool("kicad_symbol_library_list", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            libraryNickname = libraryId!.LibraryNickname
        });
        RequireToolSuccess(symbolLibraryList);
        Assert.IsTrue(symbolLibraryList.GetProperty("structuredContent").GetProperty("symbolNames")
            .EnumerateArray().Any(name => name.GetString() == libraryId.EntryName), symbolLibraryList.GetRawText());
        var symbolLibraryRead = await symbolLibraryMcp.Tool("kicad_symbol_library_read", new
        {
            instanceId, expectedProcessEpoch = client.Epoch,
            libraryNickname = libraryId.LibraryNickname, symbolName = libraryId.EntryName
        });
        RequireToolSuccess(symbolLibraryRead);
        Assert.AreEqual(libraryId.EntryName, symbolLibraryRead.GetProperty("structuredContent")
            .GetProperty("definition").GetProperty("id").GetProperty("entryName").GetString());
        string existingKey = fixtureSymbol.LibName.Length != 0 ? fixtureSymbol.LibName
            : libraryId.LibraryNickname + ":" + libraryId.EntryName;
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

    private static async Task VerifyTypedNativeCacheTransaction(NativeClient client, DocumentSpecifier document,
        string evidence, string instanceId, CancellationToken token)
    {
        string documentJson = SchematicJson.Formatter.Format(document);
        string Evidence(string phase) => Path.Combine(evidence, instanceId + "-typed-cache-" + phase);
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            Evidence("mcp-state"), Evidence("mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        async Task<CheckedSchematicState> Capture(string phase)
        {
            var reply = await mcp.Tool("kicad_schematic_checked_state", new { instanceId, documentJson });
            RequireToolSuccess(reply);
            string json = reply.GetProperty("structuredContent").GetRawText();
            await File.WriteAllTextAsync(Evidence(phase + ".json"), json, token);
            return SchematicJson.Parser.Parse<CheckedSchematicState>(json);
        }
        static string Definitions(IEnumerable<SchematicCachedSymbol> symbols) =>
            "[" + string.Join(',', symbols.Select(symbol => SchematicJson.Formatter.Format(symbol))) + "]";
        object Request(CheckedSchematicState state, SchematicScreenData screen, string definitions,
            string operationId, bool legacy = false) => new
        {
            instanceId, documentJson, screenId = screen.Metadata.ScreenId.Value, definitionsJson = definitions, operationId,
            expectedStateJson = legacy ? SchematicJson.Formatter.Format(state.State) : SchematicJson.Formatter.Format(state)
        };
        async Task<CheckedSchematicBatchReceipt> Replace(object request, string phase)
        {
            var reply = await mcp.Tool("kicad_schematic_library_cache_replace", request);
            await File.WriteAllTextAsync(Evidence(phase + "-reply.json"), reply.GetRawText(), token);
            RequireToolSuccess(reply);
            var data = reply.GetProperty("structuredContent");
            Assert.IsFalse(data.GetProperty("designFilesSaved").GetBoolean());
            var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(data.GetProperty("receipt").GetRawText());
            Assert.AreEqual(document, receipt.Document);
            Assert.AreEqual(client.Epoch, receipt.ProcessEpoch);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status);
            return receipt;
        }
        async Task<CheckedSchematicState> SaveAndReload(CheckedSchematicState expected, string phase)
        {
            var reply = await mcp.Tool("kicad_document_save", new
            {
                instanceId, expectedStateJson = SchematicJson.Formatter.Format(expected.State),
                operationId = Guid.NewGuid().ToString("D")
            });
            await File.WriteAllTextAsync(Evidence(phase + "-save.json"), reply.GetRawText(), token);
            RequireToolSuccess(reply);
            var saved = SchematicJson.Parser.Parse<LifecycleOperationResult>(reply.GetProperty("structuredContent").GetRawText());
            Assert.AreEqual(LifecycleOperationStatus.LosSaved, saved.Status);
            Assert.AreEqual(document, saved.Document);
            Assert.AreEqual(client.Epoch, saved.ProcessEpoch);
            Assert.IsFalse(saved.ObservedState.NativeContentDirty, "Reload only the confirmed clean saved state.");
            var beforeReload = await Capture(phase + "-saved");
            Assert.AreEqual(saved.ObservedState, beforeReload.State);
            // RevertDocument reloads the complete hierarchy from the files the checked MCP save just wrote.
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
            var reopened = await Capture(phase + "-reopened");
            Assert.AreEqual(beforeReload.State.SaveStableStateSha256, reopened.State.SaveStableStateSha256,
                "The whole saved hierarchy must retain its native save-stable state after reload.");
            Assert.AreEqual(beforeReload.Electrical.Hierarchy.Data, reopened.Electrical.Hierarchy.Data,
                NativeSnapshotDifference.Describe(beforeReload.Electrical.Hierarchy.Data, reopened.Electrical.Hierarchy.Data));
            return reopened;
        }

        var original = await Capture("original");
        var root = original.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document));
        Assert.IsNotEmpty(root.CachedSymbols, "The existing native fixture supplies a complete symbol definition to copy.");
        var sharedInstances = original.Electrical.Hierarchy.Data.Instances
            .Where(s => s.Metadata.Document.SheetPath.Path.Count > 1)
            .GroupBy(s => s.Metadata.ScreenId.Value).Single(g => g.Count() > 1)
            .OrderBy(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal).ToArray();
        var child = sharedInstances[0];
        foreach (var instance in sharedInstances.Skip(1))
            CollectionAssert.AreEqual(child.CachedSymbols.ToArray(), instance.CachedSymbols.ToArray());

        // Compatibility: the lifecycle-only root request still changes its cache and an exact retry
        // returns its original native receipt even though the live revision has advanced.
        var legacyDefinition = root.CachedSymbols[0].Clone();
        legacyDefinition.CacheKey = "AutomationCache:LegacyRoot";
        legacyDefinition.Definition.Id = new() { LibraryNickname = "AutomationCache", EntryName = "LegacyRoot" };
        var legacyRequest = Request(original, root, Definitions(root.CachedSymbols.Append(legacyDefinition)),
            Guid.NewGuid().ToString("D"), legacy: true);
        var legacyReceipt = await Replace(legacyRequest, "legacy-root");
        Assert.IsTrue(legacyReceipt.Result.LibraryCacheChanged);
        Assert.AreEqual(legacyReceipt, await Replace(legacyRequest, "legacy-root-replay"));
        var afterLegacy = await Capture("after-legacy-root");
        Assert.AreEqual(legacyReceipt.ObservedAfter, afterLegacy.State);
        await Replace(Request(afterLegacy, root, Definitions(root.CachedSymbols), Guid.NewGuid().ToString("D"), legacy: true),
            "restore-legacy-root");
        var before = await Capture("before-child");
        var childDefinition = root.CachedSymbols[0].Clone();
        childDefinition.CacheKey = "AutomationCache:SharedChild";
        childDefinition.Definition.Id = new() { LibraryNickname = "AutomationCache", EntryName = "SharedChild" };
        var childRequest = Request(before, child, Definitions(child.CachedSymbols.Append(childDefinition)), Guid.NewGuid().ToString("D"));
        var childReply = await mcp.Tool("kicad_schematic_library_cache_replace", childRequest);
        await File.WriteAllTextAsync(Evidence("child-reply.json"), childReply.GetRawText(), token);
        RequireToolSuccess(childReply);
        var childData = childReply.GetProperty("structuredContent");
        Assert.AreEqual(child.Metadata.Document, SchematicJson.Parser.Parse<DocumentSpecifier>(childData.GetProperty("targetDocument").GetRawText()));
        var childReceipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(childData.GetProperty("receipt").GetRawText());
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, childReceipt.Status);
        Assert.AreEqual(before.State, childReceipt.ObservedBefore);
        Assert.IsTrue(childReceipt.Result.LibraryCacheChanged);
        Assert.IsFalse(childData.GetProperty("designFilesSaved").GetBoolean());
        var changed = await Capture("changed-child");
        Assert.AreEqual(childReceipt.ObservedAfter, changed.State);
        foreach (var instance in changed.Electrical.Hierarchy.Data.Instances.Where(s => s.Metadata.ScreenId.Equals(child.Metadata.ScreenId)))
            CollectionAssert.AreEqual(child.CachedSymbols.Append(childDefinition).ToArray(), instance.CachedSymbols.ToArray());
        var replay = await mcp.Tool("kicad_schematic_library_cache_replace", childRequest);
        RequireToolSuccess(replay);
        Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
        Assert.IsFalse(replay.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual(childReceipt, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(
            replay.GetProperty("structuredContent").GetProperty("receipt").GetRawText()));
        Assert.AreEqual(changed, await Capture("after-child-replay"));
        var stale = await mcp.Tool("kicad_schematic_library_cache_replace",
            Request(before, child, Definitions(child.CachedSymbols), Guid.NewGuid().ToString("D")));
        await File.WriteAllTextAsync(Evidence("stale-reply.json"), stale.GetRawText(), token);
        Assert.IsTrue(stale.GetProperty("isError").GetBoolean());
        Assert.AreEqual("stale_document_state", stale.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.IsFalse(stale.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual(changed, await Capture("after-stale"));
        var reopened = await SaveAndReload(changed, "child");
        foreach (var instance in reopened.Electrical.Hierarchy.Data.Instances.Where(s => s.Metadata.ScreenId.Equals(child.Metadata.ScreenId)))
            CollectionAssert.AreEqual(child.CachedSymbols.Append(childDefinition).ToArray(), instance.CachedSymbols.ToArray());
        await Replace(Request(reopened, child, Definitions(child.CachedSymbols), Guid.NewGuid().ToString("D")), "restore-child");
        var restored = await SaveAndReload(await Capture("restored"), "restored");
        Assert.AreEqual(original.State.SaveStableStateSha256, restored.State.SaveStableStateSha256,
            "The focused cache journey restores the existing fixture before handing it back.");
    }
}
