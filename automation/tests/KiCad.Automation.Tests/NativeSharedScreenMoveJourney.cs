using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySharedScreenConnectedMove(NativeClient client, DocumentSpecifier root,
        HierarchyFixture hierarchy, int processId, string display, string evidence, string instanceId, bool moveSecond,
        CancellationToken token)
    {
        int targetIndex = moveSecond ? 1 : 0;
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = hierarchy.Second });
        var sourceState = await Capture();
        var source = sourceState.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var create = new ApplySchematicItemBatch { Document = first.Clone(), Description = "Create matching-unit repeated-sheet fixture" };
        var symbols = source.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).OrderBy(s => s.Position.XNm).ThenBy(s => s.Position.YNm).ToArray();
        Assert.AreEqual(2, symbols.Length);
        var ids = new List<string>(); var pins = new List<string>();
        for (int i = 0; i < symbols.Length; i++)
        {
            var symbol = symbols[i].Clone(); symbol.Id.Value = Guid.NewGuid().ToString("D"); ids.Add(symbol.Id.Value);
            symbol.Path = first.SheetPath.Clone(); symbol.ReferenceField.Text.Text_ = "TP" + (101 + i);
            symbol.InstanceRecords = new SymbolSheetRecords();
            foreach (var (document, reference) in new[] { (first, "TP" + (101 + i)), (second, "TP" + (201 + i)) })
            {
                var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = reference, Unit = 1,
                    Variants = new SchematicSymbolVariants() };
                record.Path.Add(document.SheetPath.Path.Select(p => p.Clone())); symbol.InstanceRecords.Records.Add(record);
            }
            foreach (var item in symbol.Definition.Items.Where(item => item.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = item.Item.Unpack<SchematicPin>();
                if (pin.LibraryPinId is not null) { pin.Id.Value = Guid.NewGuid().ToString("D"); pins.Add(pin.Id.Value); }
                item.Item = Any.Pack(pin);
            }
            create.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
        }
        foreach (var item in source.Items.Where(i => i.Is(SchematicLine.Descriptor)))
        {
            var line = item.Unpack<SchematicLine>();
            if (line.Type != SchematicLineType.SltWire) continue;
            line.Id.Value = Guid.NewGuid().ToString("D");
            create.Operations.Add(new SchematicItemOperation { Create = Any.Pack(line) });
        }
        Assert.AreEqual(2, pins.Count);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(create, token);
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = first }, token);
        var header = new ItemHeader { Document = first };
        var select = new AddToSelection { Header = header }; select.Items.Add(new KIID { Value = hierarchy.TextId });
        await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        var before = await Capture(); var visible = await Observe();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-before.xml"), SchematicDataXml.Write(before.Electrical.Hierarchy.Data), token);
        var selection = await Selection();
        var pair = new[] { first, second };
        foreach (var document in pair)
        {
            var connected = before.Electrical.Nets.Where(net => net.Sheets.Any(s => s.Path.Equals(document.SheetPath)
                && s.Items.Any(id => id.Value == pins[0]))).ToArray();
            Assert.AreEqual(1, connected.Length);
            Assert.IsTrue(connected[0].Sheets.Any(s => s.Path.Equals(document.SheetPath) && s.Items.Any(id => id.Value == pins[1])));
        }
        string[] topology = PinPartitions(before.Electrical);
        Assert.AreEqual("TP" + (101 + targetIndex), Symbol(before, first).ReferenceField.Text.Text_);
        Assert.AreEqual("TP" + (201 + targetIndex), Symbol(before, second).ReferenceField.Text.Text_);

        CheckedSchematicBatch Move(CheckedSchematicState baseline)
        {
            var batch = new ApplySchematicItemBatch { Document = root.Clone(), DocumentEpoch = baseline.State.Revision.Epoch,
                ExpectedRevision = baseline.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Move through the other repeated instance" };
            var movement = new SchematicConnectedSymbolMove { Delta = new() { XNm = 2540000, YNm = 2540000 } };
            movement.Symbols.Add(new KIID { Value = ids[targetIndex] });
            batch.Operations.Add(new SchematicItemOperation { TargetDocument = second.Clone(), MoveConnectedSymbols = movement });
            return new() { Batch = batch, ExpectedState = baseline.State.Clone() };
        }
        async Task PreserveHumanContext(bool historyMayHighlightTarget = false)
        {
            var current = await Observe();
            Assert.AreEqual(visible.Preview.Document, current.Preview.Document);
            Assert.AreEqual(visible.Preview.Viewport, current.Preview.Viewport);
            var actualSelection = await Selection();
            if (!historyMayHighlightTarget) Assert.AreEqual(selection, actualSelection);
            else
            {
                // Native undo may highlight the restored principal symbol.
                // It must retain the unrelated selected text and must not
                // select another component as a side effect.
                var originalIds = SchematicItemDelta.Index(selection.Items).Keys.ToHashSet();
                var actualIds = SchematicItemDelta.Index(actualSelection.Items).Keys.ToHashSet();
                var allowed = originalIds.Append(Guid.Parse(ids[targetIndex])).ToHashSet();
                Assert.IsTrue(originalIds.IsSubsetOf(actualIds));
                Assert.IsTrue(actualIds.IsSubsetOf(allowed));
            }
            var actual = await Capture();
            Assert.AreEqual("TP" + (101 + targetIndex), Symbol(actual, first).ReferenceField.Text.Text_);
            Assert.AreEqual("TP" + (201 + targetIndex), Symbol(actual, second).ReferenceField.Text.Text_);
            CollectionAssert.AreEqual(topology, PinPartitions(actual.Electrical));
            Assert.AreEqual(sourceState.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root)),
                actual.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root)));
        }

        var failed = Move(before); failed.Batch.Operations.Add(new SchematicItemOperation());
        Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted,
            (await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(failed, token)).Status);
        Assert.AreEqual(before, await Capture()); await PreserveHumanContext();
        Assert.AreEqual(visible.Preview.Png, (await Observe()).Preview.Png);
        var request = Move(before);
        var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
        var moved = await Capture();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-moved.xml"), SchematicDataXml.Write(moved.Electrical.Hierarchy.Data), token);
        foreach (var document in pair)
        {
            Assert.AreEqual(Symbol(before, document).Position.XNm + 2540000, Symbol(moved, document).Position.XNm);
            Assert.AreEqual(Symbol(before, document).Position.YNm + 2540000, Symbol(moved, document).Position.YNm);
            Assert.AreEqual(Symbol(before, document).InstanceRecords, Symbol(moved, document).InstanceRecords);
        }
        await PreserveHumanContext();
        Assert.AreNotEqual(visible.Preview.Png, (await Observe()).Preview.Png,
            "The shared physical objects legitimately move in the visible instance, without changing its identity or viewport.");
        Assert.AreEqual(receipt, await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token));
        Assert.AreEqual(moved, await Capture());
        Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted,
            (await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(Move(before), token)).Status);
        Assert.AreEqual(moved, await Capture());
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-shared-move.png"), (await Observe()).Preview.Png.ToByteArray(), token);
        await FocusedSchematicShortcut(client, first, processId, display, "z", token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, deadline.Token)).State.Revision.Equals(moved.State.Revision))
            await Task.Delay(50, deadline.Token);
        var undone = await Capture();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-undone.xml"), SchematicDataXml.Write(undone.Electrical.Hierarchy.Data), token);
        var undoDifference = SchematicHierarchyDelta.Plan(before.Electrical.Hierarchy.Data, undone.Electrical.Hierarchy.Data, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-undo-difference.json"),
            "[" + string.Join(",", undoDifference.Select(operation => SchematicJson.Formatter.Format(operation))) + "]", token);
        Assert.AreEqual(0, undoDifference.Count, "Native undo must restore all shared-screen properties; see shared-undo-difference.json.");
        await PreserveHumanContext(historyMayHighlightTarget: true);
        await FocusedSchematicShortcut(client, first, processId, display, "y", token);
        using var redoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        redoDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, redoDeadline.Token)).State.Revision.Equals(undone.State.Revision))
            await Task.Delay(50, redoDeadline.Token);
        var redone = await Capture();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-redone.xml"), SchematicDataXml.Write(redone.Electrical.Hierarchy.Data), token);
        var redoDifference = SchematicHierarchyDelta.Plan(moved.Electrical.Hierarchy.Data, redone.Electrical.Hierarchy.Data, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-redo-difference.json"),
            "[" + string.Join(",", redoDifference.Select(operation => SchematicJson.Formatter.Format(operation))) + "]", token);
        Assert.AreEqual(0, redoDifference.Count, "Native redo must restore the exact moved shared-screen geometry.");
        await PreserveHumanContext(historyMayHighlightTarget: true);
        selection = await Selection(); // Verified native history result is the next tool's starting selection.
        await using (var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-shared-mcp-state"),
            Path.Combine(evidence, instanceId + "-shared-mcp.log"), token))
        {
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var arguments = new { instanceId, documentJson = SchematicJson.Formatter.Format(second), symbolIds = new[] { ids[targetIndex] },
                deltaXNm = 1270000L, deltaYNm = 0L, documentEpoch = redone.State.Revision.Epoch,
                expectedRevision = redone.State.Revision.Sequence, operationId = Guid.NewGuid().ToString("D") };
            var toolResult = await mcp.Tool("kicad_schematic_move_connected_symbols", arguments);
            RequireToolSuccess(toolResult);
            Assert.AreEqual("Completed", toolResult.GetProperty("structuredContent").GetProperty("status").GetString());
            var fromTool = await Capture();
            foreach (var document in pair)
            {
                Assert.AreEqual(Symbol(redone, document).Position.XNm + 1270000, Symbol(fromTool, document).Position.XNm);
                Assert.AreEqual(Symbol(redone, document).Position.YNm, Symbol(fromTool, document).Position.YNm);
            }
            await PreserveHumanContext();
            RequireToolSuccess(await mcp.Tool("kicad_schematic_move_connected_symbols", arguments));
            Assert.AreEqual(fromTool, await Capture());
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-move-result.json"), JsonSerializer.Serialize(new
        { instanceId, targetIndex, matchingUnitRepeatedSheetMove = true, bothInstancePinPartitionsPreserved = true,
            visibleInstanceReferencesAndSelectionPreserved = true, partialBatchRolledBack = true,
            sameOperationReplay = true, staleRejected = true, nativeKeyboardUndo = true, nativeKeyboardRedo = true,
            normalStdioMcpSharedMoveAndReplay = true,
            differentUnitGeometryQualified = false, crossPlatformReady = false }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        Task<SchematicObservation> Observe() => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = first }, token);
        Task<SelectionResponse> Selection() => client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token);
        SchematicSymbolInstance Symbol(CheckedSchematicState state, DocumentSpecifier document) => state.Electrical.Hierarchy.Data.Instances
            .Single(s => s.Metadata.Document.Equals(document)).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == ids[targetIndex]);
    }

    private static string[] PinPartitions(SchematicElectricalState state)
    {
        string Path(SheetPath path) => string.Join('/', path.Path.Select(p => p.Value));
        var pins = state.Hierarchy.Data.Instances.SelectMany(screen => screen.Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .SelectMany(symbol => symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)
                    && (i.Unit is null || i.Unit.Unit == 0 || i.Unit.Unit == symbol.Unit.Unit))
                .Select(i => i.Item.Unpack<SchematicPin>()).Where(pin => pin.LibraryPinId is not null)
                .Select(pin => Path(screen.Metadata.Document.SheetPath) + "#" + pin.Id.Value))).ToHashSet(StringComparer.Ordinal);
        var groups = state.Nets.Select(net => net.Sheets.SelectMany(sheet => sheet.Items.Select(id => Path(sheet.Path) + "#" + id.Value))
                .Where(pins.Contains).Order(StringComparer.Ordinal).ToArray()).Where(group => group.Length != 0)
            .ToList();
        var assigned = groups.SelectMany(group => group).ToHashSet(StringComparer.Ordinal);
        groups.AddRange(pins.Where(pin => !assigned.Contains(pin)).Select(pin => new[] { pin }));
        return groups.Select(group => string.Join("|", group)).Order(StringComparer.Ordinal).ToArray();
    }
}
