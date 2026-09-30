using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyConnectedTransforms(NativeClient client, DocumentSpecifier root,
        DocumentSpecifier visibleDocument, DocumentSpecifier target, string symbolId, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        var results = new List<object>();
        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-transform-mcp-state"),
            Path.Combine(evidence, instanceId + "-transform-mcp.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        foreach (var kind in new[] { SchematicConnectedTransformKind.SctRotateClockwise, SchematicConnectedTransformKind.SctRotateCounterclockwise,
                     SchematicConnectedTransformKind.SctMirrorLeftRight, SchematicConnectedTransformKind.SctMirrorUpDown })
        {
            Console.WriteLine($"Native transform {instanceId}: {kind}");
            var before = await Capture(); var symbol = Symbol(before, target);
            var pivot = symbol.Position.Clone();
            var viewport = await Observe();
            var selection = await client.InvokeAsync<Kiapi.Common.Commands.GetSelection, Kiapi.Common.Commands.SelectionResponse>(
                new() { Header = new() { Document = visibleDocument } }, token);
            var topology = PinPartitions(before.Electrical);
            CheckedSchematicBatch Request(CheckedSchematicState state, SchematicConnectedTransformKind operationKind)
            {
                var batch = new ApplySchematicItemBatch { Document = root.Clone(), DocumentEpoch = state.State.Revision.Epoch,
                    ExpectedRevision = state.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Connected transform " + operationKind };
                var transform = new SchematicConnectedSymbolTransform { Pivot = pivot.Clone(), Kind = operationKind };
                transform.Symbols.Add(new KIID { Value = symbolId });
                batch.Operations.Add(new SchematicItemOperation { TargetDocument = target.Clone(), TransformConnectedSymbols = transform });
                return new() { Batch = batch, ExpectedState = state.State.Clone() };
            }
            foreach (int invalid in new[] { 0, 1, 2, 3 })
            {
                var bad = Request(before, invalid == 0 ? SchematicConnectedTransformKind.SctUnknown : kind);
                if (invalid == 1) bad.Batch.Operations[0].TransformConnectedSymbols.Pivot.XNm = 1;
                if (invalid == 2) bad.Batch.Operations.Add(new SchematicItemOperation());
                if (invalid == 3)
                {
                    var locked = Symbol(before, visibleDocument).Clone(); locked.Locked = LockedState.LsLocked;
                    bad.Batch.Operations.Insert(0, new SchematicItemOperation { TargetDocument = visibleDocument.Clone(), Update = Any.Pack(locked) });
                }
                var rejected = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(bad, token);
                Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted, rejected.Status);
                Assert.AreEqual(before, await Capture());
                var rejectedSelection = await client.InvokeAsync<Kiapi.Common.Commands.GetSelection, Kiapi.Common.Commands.SelectionResponse>(
                    new() { Header = new() { Document = visibleDocument } }, token);
                Assert.IsTrue(SchematicItemDelta.Index(selection.Items).Keys.ToHashSet().SetEquals(SchematicItemDelta.Index(rejectedSelection.Items).Keys),
                    $"Rejected transform case {invalid} must retain selected identities.");
            }
            var request = Request(before, kind);
            var result = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson = SchematicJson.Formatter.Format(request) });
            RequireToolSuccess(result);
            var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(result.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
            var after = await Capture();
            CollectionAssert.AreEqual(topology, PinPartitions(after.Electrical));
            foreach (var document in new[] { visibleDocument, target })
            {
                var old = Symbol(before, document); var changed = Symbol(after, document);
                Assert.AreEqual(old.InstanceRecords, changed.InstanceRecords); Assert.AreEqual(old.Definition, changed.Definition);
                Assert.AreEqual(old.LibraryId, changed.LibraryId); Assert.AreEqual(old.PinMapOverride, changed.PinMapOverride);
                Assert.AreEqual(old.ReferenceField.Text.Text_, changed.ReferenceField.Text.Text_);
                Assert.AreEqual(old.Position, changed.Position);
                foreach (int unit in new[] { 1, 2 })
                    Assert.AreEqual(TransformPoint(PinPosition(old, unit), pivot, kind), PinPosition(changed, unit), "Native transform must move actual unit pins by the stated operation.");
            }
            Assert.AreNotEqual(symbol.Transform, Symbol(after, target).Transform);
            var observation = await Observe();
            Assert.AreEqual(viewport.Preview.Viewport, observation.Preview.Viewport);
            var currentSelection = await client.InvokeAsync<Kiapi.Common.Commands.GetSelection, Kiapi.Common.Commands.SelectionResponse>(
                new() { Header = new() { Document = visibleDocument } }, token);
            Assert.IsTrue(SchematicItemDelta.Index(selection.Items).Keys.ToHashSet().SetEquals(SchematicItemDelta.Index(currentSelection.Items).Keys),
                "Selection means the same object identities, not unchanged geometry of a selected transform target.");
            Assert.AreEqual(receipt, await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token));
            Assert.AreEqual(after, await Capture());
            Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted,
                (await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(Request(before, kind), token)).Status);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-" + kind + ".png"), observation.Preview.Png.ToByteArray(), token);
            foreach (var (key, expected) in new[] { ("z", before), ("y", after), ("z", before) })
            {
                var previous = await Capture();
                await FocusedSchematicShortcut(client, visibleDocument, processId, display, key, token);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
                while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                    { Document = root.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(previous.State.Revision))
                    await Task.Delay(50, limit.Token);
                var actual = await Capture();
                var difference = SchematicHierarchyDelta.Plan(expected.Electrical.Hierarchy.Data, actual.Electrical.Hierarchy.Data, token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-" + kind + "-" + key + "-difference.json"),
                    "[" + string.Join(",", difference.Select(o => SchematicJson.Formatter.Format(o))) + "]", token);
                Assert.IsEmpty(difference); CollectionAssert.AreEqual(topology, PinPartitions(actual.Electrical));
            }
            var publicBefore = await Capture();
            var arguments = new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(target), symbolIds = new[] { symbolId },
                transform = kind switch
                {
                    SchematicConnectedTransformKind.SctRotateClockwise => "Clockwise",
                    SchematicConnectedTransformKind.SctRotateCounterclockwise => "Counterclockwise",
                    SchematicConnectedTransformKind.SctMirrorLeftRight => "MirrorLeftRight",
                    _ => "MirrorUpDown"
                },
                pivotXNm = pivot.XNm, pivotYNm = pivot.YNm, documentEpoch = publicBefore.State.Revision.Epoch,
                expectedRevision = publicBefore.State.Revision.Sequence, operationId = Guid.NewGuid().ToString("D")
            };
            var publicResult = await mcp.Tool("kicad_schematic_transform_connected_symbols", arguments);
            RequireToolSuccess(publicResult);
            var publicAfter = await Capture();
            CollectionAssert.AreEqual(topology, PinPartitions(publicAfter.Electrical));
            Assert.AreEqual(Symbol(after, target).Transform, Symbol(publicAfter, target).Transform);
            RequireToolSuccess(await mcp.Tool("kicad_schematic_transform_connected_symbols", arguments));
            Assert.AreEqual(publicAfter, await Capture());
            await FocusedSchematicShortcut(client, visibleDocument, processId, display, "z", token);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                    { Document = root.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(publicAfter.State.Revision))
                    await Task.Delay(50, limit.Token);
            }
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(publicBefore.Electrical.Hierarchy.Data,
                (await Capture()).Electrical.Hierarchy.Data, token));
            results.Add(new { kind = kind.ToString(), nativePinsTransformed = true, allInstancesConnected = true, publicMcpTransform = true,
                failedBatchRolledBack = true, staleRejected = true, replayedOnce = true, nativeUndoRedo = true });
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-transforms.json"), JsonSerializer.Serialize(new
        { instanceId, cases = results, xmlTransformIntegrationQualified = false, crossPlatformReady = false }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        Task<SchematicObservation> Observe() => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = visibleDocument }, token);
        SchematicSymbolInstance Symbol(CheckedSchematicState state, DocumentSpecifier document) => state.Electrical.Hierarchy.Data.Instances
            .Single(s => s.Metadata.Document.Equals(document)).Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == symbolId);
    }

    private static Vector2 PinPosition(SchematicSymbolInstance symbol, int unit)
    {
        var pin = symbol.Definition.Items.Single(i => i.Item.Is(SchematicPin.Descriptor) && i.Unit?.Unit == unit).Item.Unpack<SchematicPin>();
        long x = pin.Position.XNm, y = pin.Position.YNm;
        for (int step = 0; step < (int)symbol.Transform.Orientation - 1; step++) (x, y) = (y, -x);
        if (symbol.Transform.MirrorX) y = -y;
        if (symbol.Transform.MirrorY) x = -x;
        return new() { XNm = symbol.Position.XNm + x, YNm = symbol.Position.YNm + y };
    }
    private static Vector2 TransformPoint(Vector2 point, Vector2 pivot, SchematicConnectedTransformKind kind)
    {
        long x = point.XNm - pivot.XNm, y = point.YNm - pivot.YNm;
        (x, y) = kind switch
        {
            SchematicConnectedTransformKind.SctRotateClockwise => (-y, x),
            SchematicConnectedTransformKind.SctRotateCounterclockwise => (y, -x),
            SchematicConnectedTransformKind.SctMirrorLeftRight => (-x, y),
            SchematicConnectedTransformKind.SctMirrorUpDown => (x, -y),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new() { XNm = pivot.XNm + x, YNm = pivot.YNm + y };
    }
}
