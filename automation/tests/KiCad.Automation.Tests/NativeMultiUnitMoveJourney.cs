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
    private static async Task VerifyMultiUnitConnectedMove(NativeClient client, DocumentSpecifier root,
        HierarchyFixture hierarchy, int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = hierarchy.Second });
        var source = (await Capture()).Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var template = source.Items.First(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        string[] libraryPins = [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")];
        var positions = new (long X, long Y)[] { (50_000_000, 130_000_000), (80_000_000, 130_000_000), (52_540_000, 132_540_000) };
        var ids = new List<string>(); var pinIds = new Dictionary<(int Symbol, int Unit), string>();
        var create = new ApplySchematicItemBatch { Document = first.Clone(), Description = "Create distinct unit pin rows" };
        for (int index = 0; index < positions.Length; index++)
        {
            var symbol = template.Clone();
            symbol.Id.Value = Guid.NewGuid().ToString("D"); ids.Add(symbol.Id.Value);
            long dx = positions[index].X - symbol.Position.XNm, dy = positions[index].Y - symbol.Position.YNm;
            foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField, symbol.DatasheetField, symbol.DescriptionField }.Concat(symbol.UserFields))
                if (field?.Text?.Position is { } position) { position.XNm += dx; position.YNm += dy; }
            symbol.Position = new() { XNm = positions[index].X, YNm = positions[index].Y };
            // The preceding journey may have rotated the source template. These
            // new fixture wires declare unrotated local pin rows explicitly.
            symbol.Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 };
            symbol.Path = first.SheetPath.Clone(); symbol.Unit = new() { Unit = 1 };
            symbol.ReferenceField.Text.Text_ = "MU" + (301 + index); symbol.ValueField.Text.Text_ = "Unit probe";
            symbol.Definition.Id.EntryName = "UnitProbe"; symbol.Definition.UnitCount = 2;
            symbol.Definition.PinsUseLocalCoordinates = true;
            symbol.LibraryId = symbol.Definition.Id.Clone(); symbol.LibName = "";
            symbol.Definition.ReferenceField.Text.Text_ = "MU";
            symbol.Definition.ValueField.Text.Text_ = "Unit probe";
            var pinTemplate = symbol.Definition.Items.Single(i => i.Item.Is(SchematicPin.Descriptor)).Clone();
            foreach (var item in symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)).ToArray()) symbol.Definition.Items.Remove(item);
            for (int unit = 1; unit <= 2; unit++)
            {
                var item = pinTemplate.Clone(); var pin = item.Item.Unpack<SchematicPin>();
                pin.Id.Value = Guid.NewGuid().ToString("D"); pinIds[(index, unit)] = pin.Id.Value;
                pin.LibraryPinId = new() { Value = libraryPins[unit - 1] };
                pin.Number = unit.ToString(); pin.Name = unit.ToString();
                // The native protocol uses internal symbol-local coordinates
                // (positive Y down), not library-file display coordinates.
                pin.Position = new() { XNm = unit == 1 ? 0 : 5_000_000, YNm = unit == 1 ? 0 : 10_000_000 };
                item.Unit = new() { Unit = unit }; item.Item = Any.Pack(pin); symbol.Definition.Items.Add(item);
            }
            symbol.InstanceRecords = new();
            foreach (var (document, reference, unit) in new[] { (first, "MU" + (301 + index), 1), (second, "MU" + (401 + index), index == 2 ? 1 : 2) })
            {
                var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = reference, Unit = unit, Variants = new() };
                record.Path.Add(document.SheetPath.Path.Select(p => p.Clone())); symbol.InstanceRecords.Records.Add(record);
            }
            create.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
        }
        foreach (long y in new[] { 130_000_000L, 140_000_000L })
        {
            var line = source.Items.First(i => i.Is(SchematicLine.Descriptor)).Unpack<SchematicLine>();
            long offset = y == 130_000_000 ? 0 : 5_000_000;
            line.Id.Value = Guid.NewGuid().ToString("D"); line.Start = new() { XNm = 50_000_000 + offset, YNm = y }; line.End = new() { XNm = 80_000_000 + offset, YNm = y };
            create.Operations.Add(new SchematicItemOperation { Create = Any.Pack(line) });
        }
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(create, token);
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = first }, token);
        var header = new ItemHeader { Document = first };
        var group = (await Capture()).Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(first)).Items
            .Where(i => i.Is(Group.Descriptor)).Select(i => i.Unpack<Group>()).Single(g => g.Items.Any(i => i.Value == hierarchy.TextId));
        await EnterGroup();
        var before = await Capture(); var visible = await Observe(); var selection = await Selection();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-multiunit-before.xml"), SchematicDataXml.Write(before.Electrical.Hierarchy.Data), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-multiunit-before.json"), SchematicJson.Formatter.Format(before.Electrical), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-multiunit-expected.json"), JsonSerializer.Serialize(new
            { symbols = ids, pins = pinIds.Select(p => new { symbol = p.Key.Symbol, unit = p.Key.Unit, id = p.Value }),
                first = first.SheetPath.Path.Select(p => p.Value), second = second.SheetPath.Path.Select(p => p.Value) }), token);
        Assert.AreEqual(1, Symbol(before, first).Unit.Unit); Assert.AreEqual(2, Symbol(before, second).Unit.Unit);
        foreach (var (document, unit) in new[] { (first, 1), (second, 2) })
        {
            var net = before.Electrical.Nets.Single(n => n.Sheets.Any(s => s.Path.Equals(document.SheetPath) && s.Items.Any(p => p.Value == pinIds[(0, unit)])));
            Assert.IsTrue(net.Sheets.Any(s => s.Path.Equals(document.SheetPath) && s.Items.Any(p => p.Value == pinIds[(1, unit)])),
                "Unit " + unit + " must really connect its declared pin pair before testing; see multiunit-before/expected evidence.");
        }
        var topology = PinPartitions(before.Electrical);
        CheckedSchematicBatch Move(CheckedSchematicState state, long dx, long dy)
        {
            var batch = new ApplySchematicItemBatch { Document = root.Clone(), DocumentEpoch = state.State.Revision.Epoch,
                ExpectedRevision = state.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Move every active unit's connected wire" };
            var movement = new SchematicConnectedSymbolMove { Delta = new() { XNm = dx, YNm = dy } };
            movement.Symbols.Add(new KIID { Value = ids[0] });
            batch.Operations.Add(new SchematicItemOperation { TargetDocument = second.Clone(), MoveConnectedSymbols = movement });
            return new() { Batch = batch, ExpectedState = state.State.Clone() };
        }
        // Unit 2 alone would allow this position. Unit 1 would collide with a
        // separate component pin; the native all-instance guard must reject it.
        var collision = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(Move(before, 2_540_000, 2_540_000), token);
        Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted, collision.Status);
        StringAssert.Contains(collision.ErrorMessage, "pin connectivity");
        Assert.AreEqual(before, await Capture()); await HumanUnchanged();
        var failed = Move(before, 5_080_000, 5_080_000); failed.Batch.Operations.Add(new SchematicItemOperation());
        Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted,
            (await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(failed, token)).Status);
        Assert.AreEqual(before, await Capture()); await HumanUnchanged();
        var request = Move(before, 5_080_000, 5_080_000);
        var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
        var moved = await Capture();
        foreach (var document in new[] { first, second })
        {
            Assert.AreEqual(Symbol(before, document).Position.XNm + 5_080_000, Symbol(moved, document).Position.XNm);
            Assert.AreEqual(Symbol(before, document).Position.YNm + 5_080_000, Symbol(moved, document).Position.YNm);
            Assert.AreEqual(Symbol(before, document).InstanceRecords, Symbol(moved, document).InstanceRecords);
        }
        await HumanUnchanged();
        Assert.AreEqual(receipt, await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token));
        Assert.AreEqual(moved, await Capture());
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-multiunit-move.png"), (await Observe()).Preview.Png.ToByteArray(), token);
        await LeaveGroup();
        foreach (var (key, expected) in new[] { ("z", before), ("y", moved) })
        {
            var initial = await Capture(); await FocusedSchematicShortcut(client, first, processId, display, key, token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = root.Clone(), ProcessEpoch = client.Epoch }, deadline.Token)).State.Revision.Equals(initial.State.Revision))
                await Task.Delay(50, deadline.Token);
            var actual = await Capture();
            var difference = SchematicHierarchyDelta.Plan(expected.Electrical.Hierarchy.Data, actual.Electrical.Hierarchy.Data, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-multiunit-" + key + "-difference.json"),
                "[" + string.Join(",", difference.Select(o => SchematicJson.Formatter.Format(o))) + "]", token);
            Assert.IsEmpty(difference); CollectionAssert.AreEqual(topology, PinPartitions(actual.Electrical));
        }
        await EnterGroup(); selection = await Selection();
        var beforeMcp = await Capture();
        await using (var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-multiunit-mcp-state"),
            Path.Combine(evidence, instanceId + "-multiunit-mcp.log"), token))
        {
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var arguments = new { instanceId, documentJson = SchematicJson.Formatter.Format(second), symbolIds = new[] { ids[0] },
                deltaXNm = 1270000L, deltaYNm = 0L, documentEpoch = beforeMcp.State.Revision.Epoch,
                expectedRevision = beforeMcp.State.Revision.Sequence, operationId = Guid.NewGuid().ToString("D") };
            var toolResult = await mcp.Tool("kicad_schematic_move_connected_symbols", arguments);
            RequireToolSuccess(toolResult);
            Assert.AreEqual("Completed", toolResult.GetProperty("structuredContent").GetProperty("status").GetString());
            var fromMcp = await Capture();
            foreach (var document in new[] { first, second })
            {
                Assert.AreEqual(Symbol(beforeMcp, document).Position.XNm + 1270000, Symbol(fromMcp, document).Position.XNm);
                Assert.AreEqual(Symbol(beforeMcp, document).Position.YNm, Symbol(fromMcp, document).Position.YNm);
            }
            await HumanUnchanged();
            RequireToolSuccess(await mcp.Tool("kicad_schematic_move_connected_symbols", arguments));
            Assert.AreEqual(fromMcp, await Capture());
            var stale = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(Move(beforeMcp, 1270000, 0), token);
            Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted, stale.Status);
            Assert.AreEqual(fromMcp, await Capture());
        }
        await LeaveGroup();
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-multiunit-result.json"), JsonSerializer.Serialize(new
        { instanceId, differentUnitRowsMoved = true, allInstancePinPartitionsPreserved = true,
            otherInstanceShortRejected = true, failureRolledBack = true, sameOperationReplayed = true,
            nativeUndoRedo = true, normalStdioMcpAndReplay = true, enteredGroupPreserved = true,
            staleRejected = true, crossPlatformReady = false }), token);
        await VerifyConnectedTransforms(client, root, first, second, ids[0], processId, display, evidence, instanceId, token);

        async Task HumanUnchanged()
        {
            var current = await Observe(); Assert.AreEqual(visible.Preview.Viewport, current.Preview.Viewport);
            Assert.AreEqual(selection, await Selection());
            var state = await Capture();
            Assert.AreEqual("MU301", Symbol(state, first).ReferenceField.Text.Text_);
            Assert.AreEqual("MU401", Symbol(state, second).ReferenceField.Text.Text_);
            Assert.AreEqual(1, Symbol(state, first).Unit.Unit); Assert.AreEqual(2, Symbol(state, second).Unit.Unit);
            Assert.AreEqual(group, state.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(first)).Items
                .Where(i => i.Is(Group.Descriptor)).Select(i => i.Unpack<Group>()).Single(g => g.Id.Equals(group.Id)),
                "Automation-created wires must not join the group entered by the user.");
            CollectionAssert.AreEqual(topology, PinPartitions(state.Electrical));
        }
        async Task EnterGroup()
        {
            await FocusedSchematicShortcut(client, first, processId, display, "", token);
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
            var choose = new AddToSelection { Header = header }; choose.Items.Add(group.Id.Clone());
            await client.InvokeAsync<AddToSelection, SelectionResponse>(choose, token);
            await FocusedSchematicShortcut(client, first, processId, display, "J", token);
            await WaitSelection(Guid.Parse(hierarchy.TextId));
        }
        async Task LeaveGroup()
        {
            await FocusedSchematicShortcut(client, first, processId, display, "K", token);
            await WaitSelection(Guid.Parse(group.Id.Value));
        }
        async Task WaitSelection(Guid expected)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var observed = await client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, deadline.Token);
                var selected = SchematicItemDelta.Index(observed.Items).Keys.ToArray();
                if (selected.Length == 1 && selected[0] == expected) return;
                await Task.Delay(50, deadline.Token);
            }
        }
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        Task<SchematicObservation> Observe() => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = first }, token);
        Task<SelectionResponse> Selection() => client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token);
        SchematicSymbolInstance Symbol(CheckedSchematicState state, DocumentSpecifier document) => state.Electrical.Hierarchy.Data.Instances
            .Single(s => s.Metadata.Document.Equals(document)).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == ids[0]);
    }
}
