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
    private static async Task VerifyNativeSharedParentMapping(NativeClient client, DocumentSpecifier root,
        HierarchyFixture fixture, int processId, string display, string directory, string instanceId,
        Func<string, object, Task<JsonElement>> call, CancellationToken token)
    {
        var reloadFailures = new List<string>(); int reloadStep = 0;
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        DocumentSpecifier Child(DocumentSpecifier parent, KIID id)
        { var result = parent.Clone(); result.SheetPath.Path.Add(id.Clone()); return result; }
        SchematicScreenData Screen(CheckedSchematicState state, DocumentSpecifier document) =>
            state.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document));
        async Task<CheckedSchematicState> Apply(IEnumerable<SchematicItemOperation> operations, bool accepted = true)
        {
            var before = await Capture();
            var batch = new CheckedSchematicBatch { ExpectedState = before.State.Clone(), Batch = new()
            { Document = root.Clone(), DocumentEpoch = before.State.Revision.Epoch, ExpectedRevision = before.State.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), Description = "Verify explicit shared-parent instance ownership" } };
            batch.Batch.Operations.Add(operations);
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(batch, token);
            var after = await Capture();
            if (accepted) Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
            else
            {
                Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, receipt.Status, receipt.ErrorMessage);
                Assert.AreEqual(before.State.StateSha256, after.State.StateSha256, "A rejected mapping restores all native records.");
                Assert.AreEqual(before.State.Revision, after.State.Revision);
                CollectionAssert.AreEqual(PinPartitions(before.Electrical), PinPartitions(after.Electrical));
            }
            return after;
        }
        async Task History(string key, CheckedSchematicState expected)
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
            CheckedSchematicState actual;
            do
            {
                actual = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                    new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, limit.Token);
                if (actual.State.StateSha256 == expected.State.StateSha256) break;
                await Task.Delay(50, limit.Token);
            } while (true);
            CollectionAssert.AreEqual(PinPartitions(expected.Electrical), PinPartitions(actual.Electrical));
        }
        async Task<CheckedSchematicState> Reload()
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root.Clone() }, token);
            var saved = await Capture();
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
            var reopened = await Capture();
            int step = ++reloadStep;
            await File.WriteAllTextAsync(Path.Combine(directory, $"shared-parent-reload-{step}-saved.xml"),
                SchematicDataXml.Write(saved.Electrical.Hierarchy.Data), token);
            await File.WriteAllTextAsync(Path.Combine(directory, $"shared-parent-reload-{step}-reopened.xml"),
                SchematicDataXml.Write(reopened.Electrical.Hierarchy.Data), token);
            if (saved.State.SaveStableStateSha256 != reopened.State.SaveStableStateSha256)
                reloadFailures.Add($"Reload {step} changed saved digest {saved.State.SaveStableStateSha256} to {reopened.State.SaveStableStateSha256}.");
            if (!PinPartitions(saved.Electrical).SequenceEqual(PinPartitions(reopened.Electrical)))
                reloadFailures.Add($"Reload {step} changed the native pin partitions.");
            await File.WriteAllTextAsync(Path.Combine(directory, "shared-parent-reload-failures.json"),
                JsonSerializer.Serialize(reloadFailures), token);
            Assert.IsTrue(SchematicHierarchyTopology.Inspect(reopened.Electrical.Hierarchy.Data, token).IsValid,
                "A malformed reloaded hierarchy invalidates the remaining mutation path.");
            return reopened;
        }
        var original = await Capture();
        var first = Child(root, new() { Value = fixture.First });
        var second = Child(root, new() { Value = fixture.Second });
        var sheet = Screen(original, root).Items.Where(i => i.Is(SheetSymbol.Descriptor))
            .Select(i => i.Unpack<SheetSymbol>()).First().Clone();
        sheet.Id.Value = Guid.NewGuid().ToString("D"); sheet.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        sheet.Path = first.SheetPath.Clone(); sheet.NameField.Text.Text_ = "Mapped shared child";
        sheet.FilenameField.Text.Text_ = "mapped-shared-child.kicad_sch"; sheet.PageNumber = "81"; sheet.InstanceRecords = new();
        foreach (var (parent, page) in new[] { (first, "81"), (second, "82") })
        {
            var record = new SheetPlacementRecord { ProjectName = root.Project.Name, PageNumber = page, Variants = new() };
            record.Path.Add(parent.SheetPath.Path.Select(p => p.Clone())); sheet.InstanceRecords.Records.Add(record);
        }
        var childA = Child(first, sheet.Id); var childB = Child(second, sheet.Id); var unique = Child(root, sheet.Id);
        var originals = Screen(original, root).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Take(2).ToArray();
        Assert.HasCount(2, originals);
        var copies = originals.Select((s, index) => SymbolSheetOwnershipTests.PlacedCopy(s, "TP" + (801 + index), 0, 0)).ToArray();
        foreach (var (symbol, index) in copies.Select((s, i) => (s, i)))
        {
            symbol.Path = childA.SheetPath.Clone(); symbol.InstanceRecords = new();
            foreach (var (child, reference) in new[] { (childA, "TP" + (801 + index)), (childB, "TP" + (901 + index)) })
            {
                var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = reference,
                    Unit = symbol.Unit.Unit, Variants = symbol.Variants?.Clone() ?? new() };
                record.Path.Add(child.SheetPath.Path.Select(p => p.Clone())); symbol.InstanceRecords.Records.Add(record);
            }
        }
        var wire = Screen(original, root).Items.Where(i => i.Is(SchematicLine.Descriptor))
            .Select(i => i.Unpack<SchematicLine>()).First(i => i.Type == SchematicLineType.SltWire).Clone();
        wire.Id.Value = Guid.NewGuid().ToString("D");
        var setup = new List<SchematicItemOperation> { new() { TargetDocument = first.Clone(), Create = Any.Pack(sheet) } };
        setup.AddRange(copies.Select(s => new SchematicItemOperation { TargetDocument = childA.Clone(), Create = Any.Pack(s) }));
        setup.Add(new() { TargetDocument = childA.Clone(), Create = Any.Pack(wire) });
        var prepared = await Apply(setup);
        Assert.AreEqual(original.Electrical.Hierarchy.Data.Instances.Count + 2, prepared.Electrical.Hierarchy.Data.Instances.Count);
        var nestedReference = Screen(prepared, first).Items.Where(i => i.Is(SheetSymbol.Descriptor))
            .Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Equals(sheet.Id));
        var movedReference = nestedReference.Clone(); movedReference.Path = root.SheetPath.Clone();
        movedReference.PageNumber = "83"; movedReference.InstanceRecords = new();
        var movedRecord = new SheetPlacementRecord { ProjectName = root.Project.Name, PageNumber = "83", Variants = new() };
        movedRecord.Path.Add(root.SheetPath.Path.Select(p => p.Clone())); movedReference.InstanceRecords.Records.Add(movedRecord);
        var collapse = new SchematicSheetInstancePaths { SourceDocument = first.Clone(), SourceSheetId = sheet.Id.Clone(),
            DestinationDocument = root.Clone(), DestinationSheetId = sheet.Id.Clone(),
            Moves = { new SchematicSheetInstanceMove { Before = childB.SheetPath.Clone(), After = unique.SheetPath.Clone() } },
            Retired = { childA.SheetPath.Clone() } };
        SchematicItemOperation[] Move(SchematicSheetInstancePaths mapping, SheetSymbol reference) =>
        [new() { SetSheetInstancePaths = mapping }, new() { TargetDocument = mapping.SourceDocument.Clone(), Remove = mapping.SourceSheetId.Clone() },
            new() { TargetDocument = mapping.DestinationDocument.Clone(), Create = Any.Pack(reference) }];
        await Apply(Move(collapse, movedReference).Skip(1), false);
        var contradictoryPage = movedReference.Clone(); contradictoryPage.PageNumber = "2";
        await Apply(Move(collapse, contradictoryPage), false);
        var missingPageRecord = movedReference.Clone(); missingPageRecord.InstanceRecords.Records.Clear();
        await Apply(Move(collapse, missingPageRecord), false);
        var missing = collapse.Clone(); missing.Retired.Clear(); await Apply(Move(missing, movedReference), false);
        var wrongDestination = collapse.Clone();
        wrongDestination.Moves[0].After.Path.Insert(1, new() { Value = Guid.NewGuid().ToString("D") });
        await Apply(Move(wrongDestination, movedReference), false);
        await Apply([.. Move(collapse, movedReference), new SchematicItemOperation()], false);
        var collapsed = await Apply(Move(collapse, movedReference));
        var surviving = Screen(collapsed, unique).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "TP901", "TP902" }, surviving.Select(s => s.ReferenceField.Text.Text_).ToArray());
        Assert.AreEqual(original.Electrical.Hierarchy.Data.Instances.Count + 1, collapsed.Electrical.Hierarchy.Data.Instances.Count);
        await File.WriteAllTextAsync(Path.Combine(directory, "shared-parent-collapsed.xml"), SchematicDataXml.Write(collapsed.Electrical.Hierarchy.Data), token);
        await History("z", prepared); await History("y", collapsed);
        var reopened = await Reload();
        var expansion = new SchematicSheetInstancePaths { SourceDocument = root.Clone(), SourceSheetId = sheet.Id.Clone(),
            DestinationDocument = first.Clone(), DestinationSheetId = sheet.Id.Clone(),
            Moves = { new SchematicSheetInstanceMove { Before = unique.SheetPath.Clone(), After = childB.SheetPath.Clone() } },
            Added = { childA.SheetPath.Clone() } };
        // Every new instance needs its explicitly declared component records.
        await Apply(Move(expansion, nestedReference), false);
        var expandedOperations = Move(expansion, nestedReference).ToList();
        foreach (var symbol in Screen(reopened, unique).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()))
        {
            var declared = Screen(prepared, childA).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Equals(symbol.Id));
            var update = symbol.Clone(); update.Path = childA.SheetPath.Clone();
            update.InstanceRecords = declared.InstanceRecords.Clone(); update.ReferenceField = declared.ReferenceField.Clone();
            expandedOperations.Add(new() { TargetDocument = childA.Clone(), Update = Any.Pack(update) });
        }
        var expanded = await Apply(expandedOperations);
        CollectionAssert.AreEqual(PinPartitions(prepared.Electrical), PinPartitions(expanded.Electrical));
        CollectionAssert.AreEquivalent(new[] { "TP801", "TP802" }, Screen(expanded, childA).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>().ReferenceField.Text.Text_).ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "shared-parent-expanded.xml"), SchematicDataXml.Write(expanded.Electrical.Hierarchy.Data), token);
        await History("z", reopened); await History("y", expanded); await Reload();
        await VerifySharedParentXmlSynchronization(client, root, childA, childB, unique, processId, display,
            directory, instanceId, call, token);
        await Apply([new() { TargetDocument = first.Clone(), Remove = sheet.Id.Clone() },
            new() { SetReferenceInventory = Screen(original, root).Metadata.ReferenceInventory.Clone() }]);
        var restored = await Reload();
        Assert.AreEqual(original.State.SaveStableStateSha256, restored.State.SaveStableStateSha256,
            "The isolated shared-parent exercise must restore the surrounding acceptance fixture.");
        if (reloadFailures.Count != 0) Assert.Fail(string.Join("\n", reloadFailures));
        await File.WriteAllTextAsync(Path.Combine(directory, "shared-parent-proof.json"), JsonSerializer.Serialize(new
        { sourceInstances = 2, collapsedInstances = 1, expandedInstances = 2, explicitSurvivor = true, missingMappingRefused = true,
            incompleteMappingRefused = true, destinationCoverageRollback = true, lateFailureRollback = true,
            newInstanceRecordsRequired = true, nativeHistoryExact = true, reloadExact = true, originalFixtureRestored = true,
            conflictingPageRefused = true, missingPageRecordRefused = true }), token);
    }
}
