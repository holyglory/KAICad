using System.Text;
using System.Text.Json;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySynchronizationProperties(NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, string designPath, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        var saved = store.Read()!;
        var original = saved.State.Baseline;
        var before = await Capture();
        var desired = original with { Engineering = original.Engineering with { Circuit = original.Engineering.Circuit with
        {
            Components = original.Engineering.Circuit.Components.Select((c, i) => c with { Reference = "TP" + (900 + i) }).ToArray(),
            Sheets = original.Engineering.Circuit.Sheets.Select(s => s with
                { Components = s.Components.Select((c, i) => c with { Value = "Synchronization probe " + i }).ToArray() }).ToArray()
        } } };
        await SaveDesired(desired);
        Console.WriteLine($"Native synchronization {instanceId}: engineering reference/value through STDIO MCP");
        await using (var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-property-host-state"),
            Path.Combine(evidence, instanceId + "-property-host.log"), token))
        {
            RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var result = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath,
                designPath, expectedRevisionToken = saved.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(result);
            Assert.IsTrue(result.GetProperty("structuredContent").GetProperty("synchronizationCommitted").GetBoolean());
        }
        var forward = await RequireAgreement(desired.Engineering);
        Assert.AreNotEqual(before.State.Revision, forward.State.Revision);
        var oldSymbols = SchematicModelProjection.NativeSymbols(original, before.Electrical.Hierarchy.Data);
        var newSymbols = SchematicModelProjection.NativeSymbols(original, forward.Electrical.Hierarchy.Data);
        foreach (var (id, symbol) in newSymbols)
        {
            Assert.AreEqual(oldSymbols[id].Id, symbol.Id);
            Assert.AreEqual(oldSymbols[id].Definition, symbol.Definition);
            Assert.AreEqual(oldSymbols[id].LibraryId, symbol.LibraryId);
            Assert.AreEqual(oldSymbols[id].PinMapOverride, symbol.PinMapOverride);
            Assert.AreEqual(oldSymbols[id].Position, symbol.Position);
            Assert.AreEqual(oldSymbols[id].Transform, symbol.Transform);
            Assert.AreEqual(oldSymbols[id].Unit, symbol.Unit);
        }
        await RequireNoOp();

        Console.WriteLine($"Native synchronization {instanceId}: native undo conflicts with a different XML edit");
        await Shortcut("z", original.Engineering);
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
        var synchronized = saved.State.Baseline;
        var conflict = synchronized with { Engineering = synchronized.Engineering with { Circuit = synchronized.Engineering.Circuit with
            { Components = synchronized.Engineering.Circuit.Components.Select((c, i) => c with { Reference = "TP" + (800 + i) }).ToArray() } } };
        await SaveDesired(conflict);
        var conflicts = SchematicSynchronizationPlanner.Plan(saved.State);
        Assert.IsFalse(conflicts.CanPrepare);
        Assert.IsTrue(conflicts.Properties!.Conflicts.Any(c => c.Field == "reference"));
        var conflictState = await Capture();
        byte[] conflictXml = await File.ReadAllBytesAsync(designPath, token);
        await Assert.ThrowsExactlyAsync<AutomationException>(() => SchematicSynchronizationExecutor.ApplyAsync(
            store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token));
        Assert.AreEqual(conflictState, await Capture());
        CollectionAssert.AreEqual(conflictXml, await File.ReadAllBytesAsync(designPath, token));
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);

        // Withdraw only this fixture's competing XML edit, then accept the real
        // editor undo. Reverse synchronization must leave native redo intact.
        await SaveDesired(synchronized);
        var reverse = await ApplyCurrent();
        Assert.IsFalse(reverse.NativeMutationCommitted);
        await RequireAgreement(original.Engineering);
        await Shortcut("y", desired.Engineering);
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
        var redo = await ApplyCurrent();
        Assert.IsFalse(redo.NativeMutationCommitted);
        await RequireAgreement(desired.Engineering);
        await RequireNoOp();

        Console.WriteLine($"Native synchronization {instanceId}: capture native connected move and wire geometry");
        var moveBefore = await Capture();
        var movedId = original.SymbolBindings[0].NativeObjectId;
        var move = new SchematicConnectedSymbolMove { Delta = new() { XNm = 2540000, YNm = 2540000 } };
        move.Symbols.Add(new KIID { Value = movedId.ToString("D") });
        var batch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = moveBefore.State.Revision.Epoch,
            ExpectedRevision = moveBefore.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
            Description = "Native connected move for reverse synchronization" };
        batch.Operations.Add(new SchematicItemOperation { MoveConnectedSymbols = move });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var moved = await Capture();
        var symbolBefore = SchematicModelProjection.NativeSymbols(original, moveBefore.Electrical.Hierarchy.Data);
        var symbolAfter = SchematicModelProjection.NativeSymbols(original, moved.Electrical.Hierarchy.Data);
        Guid movedOccurrence = original.SymbolBindings.Single(b => b.NativeObjectId == movedId).SymbolOccurrenceId;
        Assert.AreEqual(symbolBefore[movedOccurrence].Position.XNm + move.Delta.XNm, symbolAfter[movedOccurrence].Position.XNm);
        Assert.AreEqual(symbolBefore[movedOccurrence].Position.YNm + move.Delta.YNm, symbolAfter[movedOccurrence].Position.YNm);
        foreach (var other in symbolBefore.Keys.Where(id => id != movedOccurrence))
            Assert.AreEqual(symbolBefore[other].Position, symbolAfter[other].Position);
        Assert.IsTrue(SchematicElectricalComparison.Compare(store.Read()!.State.Baseline, moved.Electrical, [], token).ConnectivityEquivalent);
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
        var movedReverse = await ApplyCurrent();
        Assert.IsFalse(movedReverse.NativeMutationCommitted);
        var movedXml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.AreEqual(SchematicModelProjection.Placement(symbolAfter[movedOccurrence]),
            movedXml.Engineering.Circuit.Symbols.Single(s => s.Id == movedOccurrence).Placement);
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(moved.Electrical.Hierarchy.Data, movedXml.Schematic, token).Count);
        Assert.AreEqual(StructuralDiagramXml.Write(original.Engineering.Structure, original.Engineering.Circuit),
            StructuralDiagramXml.Write(movedXml.Engineering.Structure, movedXml.Engineering.Circuit));
        await RequireNoOp();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-sync-properties.json"), JsonSerializer.Serialize(new
        {
            instanceId, forwardReferenceAndValueThroughStdio = true, nativeKeyboardUndoAndRedo = true,
            conflictingXmlPreserved = true, pinsAndGeometryPreservedByPropertyEdits = true,
            nativeConnectedMoveCaptured = true, nativeWireGeometryCaptured = true, pinConnectivityEquivalent = true,
            unchangedReapplication = true, testOnlyHost = true, productionToolAdvertised = false,
            forwardConnectedPlacementQualified = false, crossPlatformReady = false
        }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        async Task SaveDesired(SchematicDesign input)
        {
            byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(input, []));
            await File.WriteAllBytesAsync(designPath, xml, token);
            saved = store.Save(saved.State with { DesiredFileBytes = xml }, saved.RevisionToken);
        }
        async Task<SchematicSynchronizationExecution> ApplyCurrent() => await SchematicSynchronizationExecutor.ApplyAsync(
            store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        async Task<CheckedSchematicState> RequireAgreement(EngineeringDesign expected)
        {
            var actual = await Capture();
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.AreEqual(0, SchematicHierarchyDelta.Plan(actual.Electrical.Hierarchy.Data, xml.Schematic, token).Count);
            var bindings = SchematicDesignBindings.Inspect(xml, []);
            Assert.IsTrue(bindings.IdentitiesResolved); Assert.IsEmpty(bindings.Differences);
            foreach (var c in expected.Circuit.Components)
                Assert.AreEqual(c.Reference, xml.Engineering.Circuit.Components.Single(item => item.Id == c.Id).Reference);
            foreach (var c in expected.Circuit.Sheets.SelectMany(s => s.Components))
                Assert.AreEqual(c.Value, xml.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Single(item => item.Id == c.Id).Value);
            var connectivity = SchematicElectricalComparison.Compare(xml, actual.Electrical, [], token);
            Assert.IsTrue(connectivity.PinBindingsComplete); Assert.IsTrue(connectivity.ConnectivityEquivalent);
            return actual;
        }
        async Task RequireNoOp()
        {
            saved = store.Read()!;
            var native = await Capture(); byte[] xml = await File.ReadAllBytesAsync(designPath, token);
            DateTime timestamp = File.GetLastWriteTimeUtc(designPath);
            var result = await ApplyCurrent();
            Assert.IsFalse(result.NativeMutationCommitted); Assert.IsFalse(result.NativeFilesSaved);
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            Assert.AreEqual(native, await Capture());
            CollectionAssert.AreEqual(xml, await File.ReadAllBytesAsync(designPath, token));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(designPath));
        }
        async Task Shortcut(string key, EngineeringDesign expected)
        {
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var state = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                    new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, deadline.Token);
                var projection = original with { Engineering = expected, Schematic = state.Electrical.Hierarchy.Data };
                if (SchematicDesignBindings.Inspect(projection, []).Differences.Count == 0) break;
                await Task.Delay(50, deadline.Token);
            }
        }
    }
}
