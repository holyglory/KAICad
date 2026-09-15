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
    private static async Task VerifyOffscreenConnectedMove(NativeClient client, DocumentSpecifier root,
        ElectricalFixture fixture, HierarchyFixture hierarchy, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        var child = root.Clone(); child.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = child }, token);
        var header = new ItemHeader { Document = child.Clone() };
        var select = new AddToSelection { Header = header }; select.Items.Add(new KIID { Value = hierarchy.TextId });
        await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        var human = await Observe();
        var selection = await client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token);
        var initial = await Capture();
        var untouched = initial.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(child)).Clone();
        Assert.AreEqual(1, untouched.Items.Count(i => i.Is(SchematicLine.Descriptor)),
            "KiCad normalizes the fixture's adjacent segments when loading; preserve that actual visible native state.");
        var model = ProbeElectricalModel(initial.Electrical);
        SchematicSymbolInstance Symbol(CheckedSchematicState state) => SchematicModelProjection.NativeSymbols(model, state.Electrical.Hierarchy.Data)
            .Values.Single(s => s.Id.Value == fixture.Symbol);

        async Task HumanUnchanged()
        {
            var now = await Observe();
            Assert.AreEqual(human.Preview.Document, now.Preview.Document);
            Assert.AreEqual(human.Preview.Viewport, now.Preview.Viewport);
            Assert.AreEqual(human.Preview.Png, now.Preview.Png, "Offscreen work must not add artwork or selection overlays to the visible sheet.");
            Assert.AreEqual(selection, await client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token));
            Assert.AreEqual(untouched, (await Capture()).Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(child)),
                "Unrelated visible native objects must remain unchanged even when their rendered pixels would look identical.");
        }
        CheckedSchematicBatch Move(CheckedSchematicState before)
        {
            var batch = new ApplySchematicItemBatch { Document = root.Clone(), DocumentEpoch = before.State.Revision.Epoch,
                ExpectedRevision = before.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Move on a non-displayed sheet" };
            var movement = new SchematicConnectedSymbolMove { Delta = new() { XNm = 2540000, YNm = 2540000 } };
            movement.Symbols.Add(new KIID { Value = fixture.Symbol });
            batch.Operations.Add(new SchematicItemOperation { TargetDocument = root.Clone(), MoveConnectedSymbols = movement });
            return new() { Batch = batch, ExpectedState = before.State.Clone() };
        }
        // Reject after a real drag, proving its new bends and object changes
        // roll back without borrowing the visible selection or its screen.
        var bad = Move(initial); bad.Batch.Operations.Add(new SchematicItemOperation());
        var rejected = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(bad, token);
        Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted, rejected.Status);
        Assert.AreEqual(initial, await Capture()); await HumanUnchanged();

        var request = Move(initial);
        var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
        var moved = await Capture();
        Assert.AreEqual(Symbol(initial).Position.XNm + 2540000, Symbol(moved).Position.XNm);
        Assert.AreEqual(Symbol(initial).Position.YNm + 2540000, Symbol(moved).Position.YNm);
        Assert.IsTrue(SchematicElectricalComparison.Compare(model, moved.Electrical, [], token).ConnectivityEquivalent);
        await HumanUnchanged();
        var replay = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        Assert.AreEqual(receipt, replay); Assert.AreEqual(moved, await Capture());
        var stale = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(Move(initial), token);
        Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted, stale.Status);
        Assert.AreEqual(moved, await Capture()); await HumanUnchanged();

        await using (var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-offscreen-mcp-state"),
            Path.Combine(evidence, instanceId + "-offscreen-mcp.log"), token))
        {
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var arguments = new { instanceId, documentJson = SchematicJson.Formatter.Format(root), symbolIds = new[] { fixture.Symbol },
                deltaXNm = 2540000L, deltaYNm = 0L, documentEpoch = moved.State.Revision.Epoch,
                expectedRevision = moved.State.Revision.Sequence, operationId = Guid.NewGuid().ToString("D") };
            var toolResult = await mcp.Tool("kicad_schematic_move_connected_symbols", arguments);
            RequireToolSuccess(toolResult);
            Assert.AreEqual("Completed", toolResult.GetProperty("structuredContent").GetProperty("status").GetString());
            var fromTool = await Capture();
            Assert.AreEqual(Symbol(moved).Position.XNm + 2540000, Symbol(fromTool).Position.XNm);
            Assert.IsTrue(SchematicElectricalComparison.Compare(model, fromTool.Electrical, [], token).ConnectivityEquivalent);
            await HumanUnchanged();
            RequireToolSuccess(await mcp.Tool("kicad_schematic_move_connected_symbols", arguments));
            Assert.AreEqual(fromTool, await Capture());
            moved = fromTool;
        }

        // Run the journaled XML path against the same hidden root. Explicit
        // coordinates live in the model; the native snapshot still has old wires.
        string path = Path.Combine(evidence, instanceId + "-offscreen-design.xml");
        var baseline = ProbeElectricalModel(moved.Electrical);
        byte[] original = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(path, original, token);
        var store = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-offscreen-recovery.json"));
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(moved.State.Revision.Epoch, moved.State.Revision.Sequence),
            moved.Electrical.Hierarchy.TrackingComplete, baseline, original, moved.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: moved.Electrical.Clone(), ObservedElectrical: moved.Electrical.Clone()), null);
        await SchematicSynchronizationExecutor.ApplyAsync(store, client, path, saved.RevisionToken, Guid.NewGuid(), token);
        saved = store.Read()!; baseline = saved.State.Baseline;
        var id = baseline.SymbolBindings.Single(b => b.NativeObjectId.ToString("D") == fixture.Symbol).SymbolOccurrenceId;
        var placement = SchematicModelProjection.Placement(Symbol(moved));
        var desired = baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
            { Symbols = baseline.Engineering.Circuit.Symbols.Select(s => s.Id == id ? s with { Placement = placement with
                { XMillimeters = placement.XMillimeters + 2.54m } } : s).ToArray() } } };
        byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
        await File.WriteAllBytesAsync(path, xml, token);
        saved = store.Save(saved.State with { DesiredFileBytes = xml }, saved.RevisionToken);
        var applied = await SchematicSynchronizationExecutor.ApplyAsync(store, client, path, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsTrue(applied.SynchronizationCommitted); Assert.IsTrue(applied.NativeMutationCommitted);
        var final = await Capture();
        var result = SchematicDesignXml.Read(await File.ReadAllTextAsync(path, token), []);
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(final.Electrical.Hierarchy.Data, result.Schematic, token).Count);
        Assert.IsTrue(SchematicElectricalComparison.Compare(result, final.Electrical, [], token).ConnectivityEquivalent);
        await HumanUnchanged();
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-offscreen-visible.png"), (await Observe()).Preview.Png.ToByteArray(), token);

        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root }, token);
        var finalImage = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-offscreen-target.png"), finalImage.Preview.Png.ToByteArray(), token);
        // The original on-screen journey above already proves native undo;
        // now undo this offscreen-created commit through the rendered editor.
        await FocusedSchematicShortcut(client, root, processId, display, "z", token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
        while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root, ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(final.State.Revision))
            await Task.Delay(50, limit.Token);
        var undone = await Capture();
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(moved.Electrical.Hierarchy.Data, undone.Electrical.Hierarchy.Data, token).Count);
        Assert.IsTrue(SchematicElectricalComparison.Compare(model, undone.Electrical, [], token).ConnectivityEquivalent);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-offscreen-result.json"), JsonSerializer.Serialize(new
        { instanceId, offscreenConnectedMove = true, partialBatchRolledBack = true, staleRejected = true, retryAppliedOnce = true,
            visibleSheetViewportImageSelectionPreserved = true, visibleNativeObjectsPreserved = true,
            normalStdioMcpMoveAndReplay = true, forwardXmlApplied = true, connectivityPreserved = true,
            nativeKeyboardUndo = true, repeatedDifferentUnitQualified = false, crossPlatformReady = false }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        Task<SchematicObservation> Observe() => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new()
            { Document = child.Clone() }, token);
    }
}
