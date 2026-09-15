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
    private static async Task VerifyOffscreenTransformXml(NativeClient client, DocumentSpecifier root,
        DocumentSpecifier child, ElectricalFixture fixture, string selectedText, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        var initial = await Capture();
        var screen = initial.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var label = screen.Items.Single(i => i.Is(LocalLabel.Descriptor)).Unpack<LocalLabel>();
        var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == fixture.Symbol);
        var field = symbol.ValueField.Clone(); field.Name = "Routing preference";
        field.Text.Text_ = "Keep this label beside its connection.";
        field.Text.Position = label.Position.Clone(); field.Text.Position.YNm += 2540000;
        field.Visible = true; field.AllowAutoPlace = false; label.Fields.Add(field);
        var addField = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Add label guidance fixture" };
        addField.Operations.Add(new SchematicItemOperation { Update = Any.Pack(label) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(addField, token);
        initial = await Capture();
        var baseline = ProbeElectricalModel(initial.Electrical);
        string designPath = Path.Combine(evidence, instanceId + "-offscreen-transform.xml");
        byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(designPath, bytes, token);
        var store = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-offscreen-transform-recovery.json"));
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(initial.State.Revision.Epoch, initial.State.Revision.Sequence),
            initial.Electrical.Hierarchy.TrackingComplete, baseline, bytes, initial.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone()), null);
        await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = child }, token);
        var header = new ItemHeader { Document = child.Clone() };
        var choose = new AddToSelection { Header = header }; choose.Items.Add(new KIID { Value = selectedText });
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
        await client.InvokeAsync<AddToSelection, SelectionResponse>(choose, token);
        var visible = await Observe();
        var selected = await client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token);
        var untouched = (await Capture()).Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(child)).Clone();
        var cases = new List<object>();
        foreach (int transform in new[] { 0, 1, 2, 3 })
        {
            saved = store.Read()!; baseline = saved.State.Baseline;
            var occurrence = baseline.SymbolBindings.Single(b => b.NativeObjectId.ToString("D") == fixture.Symbol).SymbolOccurrenceId;
            var old = SchematicModelProjection.Placement(SchematicModelProjection.NativeSymbols(baseline, baseline.Schematic)[occurrence]);
            var wanted = transform switch
            {
                0 => old with { RotationDegrees = (old.RotationDegrees + 90) % 360 },
                1 => old with { MirrorY = !old.MirrorY },
                2 => old with { RotationDegrees = (old.RotationDegrees + 180) % 360 },
                _ => old with { MirrorX = !old.MirrorX, XMillimeters = old.XMillimeters + 2.54m }
            };
            var desired = baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
                { Symbols = baseline.Engineering.Circuit.Symbols.Select(s => s.Id == occurrence ? s with { Placement = wanted } : s).ToArray() } } };
            bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
            await File.WriteAllBytesAsync(designPath, bytes, token);
            saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
            Guid operation = Guid.NewGuid();
            var applied = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, operation, token);
            Assert.IsTrue(applied.SynchronizationCommitted); Assert.IsTrue(applied.NativeMutationCommitted);
            var after = await Capture();
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.IsTrue(SchematicOrientation.Equivalent(wanted,
                SchematicModelProjection.Placement(SchematicModelProjection.NativeSymbols(xml, after.Electrical.Hierarchy.Data)[occurrence])));
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(after.Electrical.Hierarchy.Data, xml.Schematic, token));
            Assert.IsTrue(SchematicElectricalComparison.Compare(xml, after.Electrical, [], token).ConnectivityEquivalent);
            LabelAttached(after);
            var currentView = await Observe();
            Assert.AreEqual(visible.Preview.Viewport, currentView.Preview.Viewport);
            Assert.AreEqual(visible.Preview.Png, currentView.Preview.Png);
            Assert.AreEqual(selected, await client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token));
            Assert.AreEqual(untouched, after.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(child)));
            var replay = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, operation, token);
            Assert.IsTrue(replay.Replayed); Assert.AreEqual(after, await Capture());
            cases.Add(new { transform, hiddenNativeAndXmlMatched = true, labelAndGuidancePreserved = true, humanViewPreserved = true, replayedOnce = true });
        }
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root }, token);
        foreach (string key in new[] { "z", "y" })
        {
            var before = await Capture();
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = root.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(before.State.Revision))
                await Task.Delay(50, limit.Token);
            saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
            var reverse = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
            Assert.IsTrue(reverse.SynchronizationCommitted); Assert.IsFalse(reverse.NativeMutationCommitted);
            var after = await Capture(); LabelAttached(after);
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(after.Electrical.Hierarchy.Data, xml.Schematic, token));
            Assert.IsTrue(SchematicElectricalComparison.Compare(xml, after.Electrical, [], token).ConnectivityEquivalent);
        }
        var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root }, token);
        Assert.AreEqual(image.Snapshot.Revision, image.Preview.Revision);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-offscreen-transform-label.png"), image.Preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-offscreen-transform-result.json"), JsonSerializer.Serialize(new
            { instanceId, cases, nativeUndoRedoToXml = true, crossPlatformReady = false }), token);

        void LabelAttached(CheckedSchematicState state)
        {
            var items = state.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items;
            var current = items.Where(i => i.Is(LocalLabel.Descriptor)).Select(i => i.Unpack<LocalLabel>()).Single(l => l.Id.Equals(label.Id));
            Assert.AreEqual(label.Text.Text_, current.Text.Text_);
            Assert.AreEqual(field.Text.Text_, current.Fields.Single(f => f.Name == field.Name).Text.Text_);
            Assert.IsTrue(items.Where(i => i.Is(SchematicLine.Descriptor)).Select(i => i.Unpack<SchematicLine>())
                .Any(line => line.Type == SchematicLineType.SltWire && OnLine(current.Position, line)), "The label must remain on its actual native wire.");
        }
        static bool OnLine(Vector2 point, SchematicLine line) =>
            (System.Numerics.BigInteger)(point.XNm - line.Start.XNm) * (line.End.YNm - line.Start.YNm)
                == (System.Numerics.BigInteger)(point.YNm - line.Start.YNm) * (line.End.XNm - line.Start.XNm)
            && point.XNm >= Math.Min(line.Start.XNm, line.End.XNm) && point.XNm <= Math.Max(line.Start.XNm, line.End.XNm)
            && point.YNm >= Math.Min(line.Start.YNm, line.End.YNm) && point.YNm <= Math.Max(line.Start.YNm, line.End.YNm);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        Task<SchematicObservation> Observe() => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = child.Clone() }, token);
    }
}
