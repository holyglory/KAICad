using System.Security.Cryptography;
using System.Text;
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
    // p195ade3ceaabb5f4 / ncb94b8bd7edcd0e1: use the already qualified repeated-screen
    // fixture, with complete engineering identities, through public synchronization.
    // Each of the three sheet instances has two explicitly connected one-pin probes.
    private static async Task VerifyRepeatedSheetRebuild(NativeClient client, DocumentSpecifier document,
        HierarchyFixture fixture, int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(evidence, instanceId + "-repeated-rebuild")).FullName;
        string FileName(string name) => Path.Combine(directory, name);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        string project = Path.Combine(fixture.Directory, "fixture.kicad_pro");
        string rootFile = Path.ChangeExtension(project, ".kicad_sch");
        string childFile = Path.Combine(fixture.Directory, "shared-child.kicad_sch");
        string unrelated = Path.Combine(fixture.Directory, "unloaded.kicad_sch");
        byte[] unrelatedBytes = await File.ReadAllBytesAsync(unrelated, token);
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        var beforeChain = await Capture();
        var chain = new SchematicNetChainState();
        chain.Definitions.Add(new SchematicNetChainDefinition { Name = "REPEATED_CHAIN",
            From = new() { Reference = "TP101", Pin = "1" }, To = new() { Reference = "TP102", Pin = "1" } });
        var chainBatch = new CheckedSchematicBatch { ExpectedState = beforeChain.State.Clone(), Batch = new()
        {
            Document = document.Clone(), DocumentEpoch = beforeChain.State.Revision.Epoch,
            ExpectedRevision = beforeChain.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
            Description = "Declare the first repeated sheet's probe chain"
        } };
        chainBatch.Batch.Operations.Add(new SchematicItemOperation { TargetDocument = document.Clone(), ReplaceNetChains = chain });
        var chained = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(chainBatch, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, chained.Status, chained.ErrorMessage);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        var reloaded = await Capture();
        var checkpoint = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(new()
        {
            Document = document.Clone(), ExpectedState = reloaded.State.Clone(), OperationId = Guid.NewGuid().ToString("D")
        }, token);
        Assert.AreEqual(LifecycleOperationStatus.LosSaved, checkpoint.Status, checkpoint.ErrorMessage);
        var original = await Capture();
        Assert.IsFalse(original.State.NativeContentDirty);
        var baseline = RepeatedProbeDesign(original.Electrical);
        var binding = SchematicDesignBindings.Inspect(baseline, []);
        Assert.IsTrue(binding.IdentitiesResolved, string.Join(", ", binding.Issues.Select(i => i.Code)));
        Assert.IsEmpty(binding.Differences);
        var comparison = SchematicElectricalComparison.Compare(baseline, original.Electrical, []);
        Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent,
            "The expected three separate two-pin nets must match native connectivity before recovery.");
        Assert.HasCount(3, PinPartitions(original.Electrical));
        Assert.IsTrue(original.Electrical.Hierarchy.Data.Instances.All(s => s.Metadata.NetChains.Single().Name == "REPEATED_CHAIN"));

        var originalFiles = new[] { rootFile, childFile, project }.ToDictionary(f => f, System.IO.File.ReadAllBytes);
        foreach (var (file, bytes) in originalFiles)
            await File.WriteAllBytesAsync(FileName("original-" + Path.GetFileName(file)), bytes, token);
        string designPath = FileName("design.xml");
        byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(designPath, xml, token);
        var store = new DesignRecoveryStore(FileName("recovery.json"));
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId),
            new(original.State.Revision.Epoch, original.State.Revision.Sequence), original.Electrical.Hierarchy.TrackingComplete,
            baseline, xml, baseline.Schematic.Clone(), [], BaselineElectrical: original.Electrical.Clone(),
            ObservedElectrical: original.Electrical.Clone()), null);
        var mcpStart = SyncHarnessProcessTests.ProductionStartInfo();
        // The companion must use KiCad's loaded-file observation, not its own environment.
        mcpStart.Environment["KAICAD_FIXTURE_SHEET_ROOT"] = Path.Combine(fixture.Directory, "wrong-agent-directory");
        mcpStart.Environment["KAICAD_FIXTURE_SHEET_LEAF"] = "wrong-agent-file.kicad_sch";
        await using var host = await StdioMcpFixture.StartAsync(mcpStart,
            FileName("mcp-state"), FileName("mcp.log"), token);
        int step = 0;
        async Task<JsonElement> Call(string name, object arguments)
        {
            var result = await host.Tool(name, arguments);
            await File.WriteAllTextAsync(FileName($"{++step:D2}-{name}.json"), RetainedToolEvidence(result), token);
            return result;
        }
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };
        RequireToolSuccess(await Call("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var settled = await Call("kicad_design_sync_plan", Recovery());
        RequireToolSuccess(settled);
        Assert.AreEqual(0, settled.GetProperty("structuredContent").GetProperty("nativeOperationsJson").GetArrayLength());
        RequireToolSuccess(await Call("kicad_document_close", new { instanceId,
            expectedStateJson = SchematicJson.Formatter.Format(original.State), operationId = Guid.NewGuid().ToString("D") }));
        System.IO.File.Delete(rootFile);
        System.IO.File.Delete(childFile);
        var created = await Call("kicad_schematic_create", new { instanceId, path = rootFile });
        RequireToolSuccess(created);
        var recreated = SchematicJson.Parser.Parse<DocumentSpecifier>(created.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.AreEqual(document, recreated);
        var empty = await Capture();
        Assert.HasCount(1, empty.Electrical.Hierarchy.Data.Instances);
        Assert.IsEmpty(empty.Electrical.Hierarchy.Data.Instances[0].Items);
        RequireToolSuccess(await Call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = saved.RevisionToken, expectedDocumentEpoch = empty.State.Revision.Epoch }));
        var plan = await Call("kicad_design_sync_plan", Recovery());
        RequireToolSuccess(plan);
        var planned = plan.GetProperty("structuredContent");
        Assert.IsTrue(planned.GetProperty("nativeRebuildRequired").GetBoolean());
        var operations = planned.GetProperty("nativeOperationsJson").EnumerateArray()
            .Select(o => SchematicJson.Parser.Parse<SchematicItemOperation>(o.GetString()!)).ToArray();
        Assert.HasCount(1, operations.Where(o => o.ReplaceNetChains is not null));
        Assert.HasCount(4, operations.Where(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true),
            "Root has two physical symbols and the repeated child has two, not four.");
        Assert.HasCount(2, operations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        Assert.AreEqual(empty, await Capture(), "Planning changes no native state.");

        // A later invalid operation must roll back staged repeated screens as one atomic batch.
        var invalid = new CheckedSchematicBatch { ExpectedState = empty.State.Clone(), Batch = new()
        {
            Document = document.Clone(), DocumentEpoch = empty.State.Revision.Epoch,
            ExpectedRevision = empty.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Refuse incomplete repeated rebuild"
        } };
        invalid.Batch.Operations.Add(operations.Select(o => o.Clone()));
        invalid.Batch.Operations.Add(new SchematicItemOperation());
        var rejected = await Call("kicad_schematic_apply_checked_batch", new { instanceId, requestJson = SchematicJson.Formatter.Format(invalid) });
        Assert.IsTrue(rejected.GetProperty("isError").GetBoolean());
        Assert.AreEqual(empty, await Capture());
        Assert.IsFalse(System.IO.File.Exists(rootFile) || System.IO.File.Exists(childFile));
        CollectionAssert.AreEqual(xml, await File.ReadAllBytesAsync(designPath, token));
        var applied = await Call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath,
            expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
        RequireToolSuccess(applied);
        var rebuilt = await Capture();
        await File.WriteAllTextAsync(FileName("original.json"), SchematicJson.Formatter.Format(original), token);
        await File.WriteAllTextAsync(FileName("rebuilt.json"), SchematicJson.Formatter.Format(rebuilt), token);
        Assert.AreEqual(original.State.StateSha256, rebuilt.State.StateSha256);
        Assert.AreEqual(original.State.SaveStableStateSha256, rebuilt.State.SaveStableStateSha256);
        Assert.AreEqual(RebuildWithoutProvenance(original.Electrical.Hierarchy.Data), RebuildWithoutProvenance(rebuilt.Electrical.Hierarchy.Data));
        CollectionAssert.AreEqual(PinPartitions(original.Electrical), PinPartitions(rebuilt.Electrical));
        Assert.IsTrue(SchematicElectricalComparison.Compare(store.Read()!.State.Baseline, rebuilt.Electrical, []).ConnectivityEquivalent);
        foreach (var (file, bytes) in originalFiles)
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file, token), Path.GetFileName(file));
        CollectionAssert.AreEqual(unrelatedBytes, await File.ReadAllBytesAsync(unrelated, token));
        Assert.IsFalse(rebuilt.State.NativeContentDirty);
        var noOp = await Call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath,
            expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
        RequireToolSuccess(noOp);
        Assert.IsFalse(noOp.GetProperty("structuredContent").GetProperty("nativeMutationCommitted").GetBoolean());
        Assert.IsFalse(noOp.GetProperty("structuredContent").GetProperty("nativeFilesSaved").GetBoolean());

        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        var undone = await Until(s => s.Electrical.Hierarchy.Data.Instances.Count == 1, "undo");
        Assert.IsEmpty(undone.Electrical.Hierarchy.Data.Instances[0].Items);
        Assert.AreEqual(empty.State.StateSha256, undone.State.StateSha256);
        await FocusedSchematicShortcut(client, document, processId, display, "y", token);
        var redone = await Until(s => s.State.StateSha256 == rebuilt.State.StateSha256, "redo");
        Assert.AreEqual(rebuilt.State.SaveStableStateSha256, redone.State.SaveStableStateSha256);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        var reopened = await Capture();
        Assert.AreEqual(original.State.StateSha256, reopened.State.StateSha256);
        CollectionAssert.AreEqual(PinPartitions(original.Electrical), PinPartitions(reopened.Electrical));
        foreach (var screen in reopened.Electrical.Hierarchy.Data.Instances)
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = screen.Metadata.Document.Clone() }, token);
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = screen.Metadata.Document.Clone() }, token);
            await File.WriteAllBytesAsync(FileName(screen.Metadata.Document.SheetPath.Path[^1].Value + ".png"), observation.Preview.Png.ToByteArray(), token);
        }
        await VerifyRepeatedNativeAdoption(client, document, fixture, processId, display, instanceId,
            designPath, store, Call, token);
        await VerifySheetOwnershipRoundTrips(client, document, fixture, processId, display, instanceId,
            designPath, store, Call, token);
        await File.WriteAllTextAsync(FileName("proof.json"), JsonSerializer.Serialize(new
        {
            instanceId, sharedPhysicalScreens = 1, sheetInstances = 3, physicalFiles = 2,
            modelBindingsComplete = true, nativeRebuild = true, failedBatchPreserved = true,
            sameStateDigest = true, filesByteIdentical = 3, samePinPartition = true,
            separateReferencesPreserved = true, netChainRestored = true, secondApplyNoOp = true,
            undoEmptyRoot = true, redoExact = true, saveReopenExact = true,
            originalFiles = originalFiles.ToDictionary(p => Path.GetFileName(p.Key), p => Convert.ToHexStringLower(SHA256.HashData(p.Value)))
        }), token);

        async Task<CheckedSchematicState> Until(Func<CheckedSchematicState, bool> condition, string name)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            CheckedSchematicState? last = null;
            try
            {
                while (true)
                {
                    last = await Capture();
                    if (condition(last)) return last;
                    await Task.Delay(50, limit.Token);
                }
            }
            finally { if (last is not null) await File.WriteAllTextAsync(FileName(name + ".json"), SchematicJson.Formatter.Format(last), CancellationToken.None); }
        }
    }

    private static SchematicDesign RepeatedProbeDesign(SchematicElectricalState state)
    {
        var data = state.Hierarchy.Data;
        Assert.HasCount(3, data.Instances);
        var definitions = data.Instances.GroupBy(s => s.Metadata.ScreenId.Value).ToDictionary(g => g.Key, g => Guid.NewGuid());
        var instances = data.Instances.ToDictionary(RebuildPathKey, _ => Guid.NewGuid());
        Guid part = Guid.NewGuid();
        var componentDefinitions = new Dictionary<string, ComponentDefinition[]>();
        foreach (var group in data.Instances.GroupBy(s => s.Metadata.ScreenId.Value))
            componentDefinitions.Add(group.Key, group.First().Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).OrderBy(s => s.Id.Value, StringComparer.Ordinal)
                .Select(s => new ComponentDefinition(Guid.NewGuid(), part, s.ValueField.Text.Text_)).ToArray());
        var components = new List<ComponentInstance>();
        var occurrences = new List<SymbolOccurrence>();
        var bindings = new List<SchematicSymbolBinding>();
        var nets = new List<CircuitNet>();
        foreach (var screen in data.Instances)
        {
            var symbols = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
            Assert.HasCount(2, symbols);
            var pair = symbols.Select((s, i) => new ComponentInstance(Guid.NewGuid(), componentDefinitions[screen.Metadata.ScreenId.Value][i].Id,
                instances[RebuildPathKey(screen)], s.ReferenceField.Text.Text_)).ToArray();
            components.AddRange(pair);
            foreach (var (symbol, component) in symbols.Zip(pair))
            {
                var occurrence = new SymbolOccurrence(Guid.NewGuid(), component.Id, 1, SchematicModelProjection.Placement(symbol));
                occurrences.Add(occurrence); bindings.Add(new(occurrence.Id, Guid.Parse(symbol.Id.Value)));
            }
            // These connections are explicitly drawn by the fixture; do not infer expected nets from native memberships.
            nets.Add(new(Guid.NewGuid(), "Probe pair " + screen.Metadata.Document.SheetPath.Path[^1].Value,
                pair.Select(c => new PinEndpoint(c.Id, "1")).ToArray()));
        }
        var circuit = new Circuit(Guid.NewGuid(), [new(part, "Fixture probe", 1, [new("1", "1", 1)])],
            definitions.Select(d => new SheetDefinition(d.Value, d.Key, componentDefinitions[d.Key])).ToArray(),
            data.Instances.Select(s => new KiCad.Automation.Model.SheetInstance(instances[RebuildPathKey(s)], definitions[s.Metadata.ScreenId.Value],
                s.Metadata.Document.SheetPath.Path.Count == 1 ? null : instances[RebuildPathKey(s)[..RebuildPathKey(s).LastIndexOf('/')]])).ToArray(),
            components, nets, occurrences);
        return new(new(circuit, new(Guid.NewGuid(), [], [], [], []), [], []), data.Clone(),
            data.Instances.Select(s => new SchematicSheetBinding(instances[RebuildPathKey(s)],
                s.Metadata.Document.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())).ToArray(), bindings);
    }
}
