using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    internal sealed record ElectricalFixture(string Contents, string Symbol, string Wire, string PinA, string PinB);

    internal static ElectricalFixture MakeElectricalFixture(string rootId)
    {
        string a = Guid.NewGuid().ToString("D"), b = Guid.NewGuid().ToString("D");
        string pinA = Guid.NewGuid().ToString("D"), pinB = Guid.NewGuid().ToString("D");
        string wire = Guid.NewGuid().ToString("D"), label = Guid.NewGuid().ToString("D");
        string definition = """
            (lib_symbols (symbol "Automation:Probe"
              (pin_names (offset 0) hide) (in_bom yes) (on_board yes)
              (property "Reference" "TP" (at 0 3 0) (effects (font (size 1.27 1.27))))
              (property "Value" "Probe" (at 0 5 0) (effects (font (size 1.27 1.27))))
              (symbol "Probe_0_1" (circle (center 0 2.54) (radius 0.75)
                (stroke (width 0) (type default)) (fill (type none)))
                (circle (center 0 2.54) (radius 0.75)
                  (stroke (width 0) (type default)) (fill (type none)))
                (arc (start 2 1) (mid 3 2) (end 4 1)
                  (stroke (width 0) (type default)) (fill (type none)))
                (rectangle (start 2 3) (end 4 4)
                  (stroke (width 0) (type default)) (fill (type none)))
                (bezier (pts (xy 2 5) (xy 3 6) (xy 4 4) (xy 5 5))
                  (stroke (width 0) (type default)) (fill (type none)))
                (polyline (pts (xy 2 7) (xy 3 8) (xy 4 7))
                  (stroke (width 0) (type default)) (fill (type none)))
                (ellipse (center 6 2) (major_radius 1) (minor_radius 0.5) (rotation_angle 0)
                  (stroke (width 0) (type default)) (fill (type none)))
                (ellipse_arc (center 6 4) (major_radius 1) (minor_radius 0.5)
                  (rotation_angle 0) (start_angle 0) (end_angle 90)
                  (stroke (width 0) (type default)) (fill (type none)))
                (text "Definition note" (at 6 6 0) (effects (font (size 1 1))))
                (text_box "Definition box" (at 6 8 0) (size 4 2) (margins 0 0 0 0)
                  (stroke (width 0) (type default)) (fill (type none))
                  (effects (font (size 1 1)))))
              (symbol "Probe_1_1" (pin passive line (at 0 0 90) (length 2.54)
                (name "1" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27))))))))
            """;
        string Symbol(string id, string pin, string reference, int x) => $$"""
            (symbol (lib_id "Automation:Probe") (at {{x}} 80 0) (unit 1)
              (in_bom yes) (on_board yes) (dnp no) (uuid {{id}})
              (property "Reference" "{{reference}}" (at {{x}} 75 0) (effects (font (size 1.27 1.27))))
              (property "Value" "Probe" (at {{x}} 73 0) (effects (font (size 1.27 1.27))))
              (pin "1" (uuid {{pin}}))
              (instances (project "fixture" (path "/{{rootId}}" (reference "{{reference}}") (unit 1)))))
            """;
        string contents = definition + Symbol(a, pinA, "TP1", 80) + Symbol(b, pinB, "TP2", 100) + $$"""
            (wire (pts (xy 80 80) (xy 100 80)) (stroke (width 0) (type default)) (uuid {{wire}}))
            (label "SIGNAL" (at 90 80 0) (effects (font (size 1.27 1.27))) (uuid {{label}}))
            """;
        return new(contents, a, wire, pinA, pinB);
    }

    private static async Task VerifyElectricalBatch(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, int processId, string display, string evidence, string instanceId,
        CancellationToken token)
    {
        var netQuery = new GetSchematicNetlist { Document = document };
        async Task<string[]> Nets() => (await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(netQuery, token))
            .Nets.Select(n => n.Name + ":" + string.Join(",", n.Sheets.SelectMany(s => s.Items)
                .Select(i => i.Value).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToArray();
        var baseline = await Nets();
        await VerifyElectricalState(client, document, fixture, evidence, instanceId, token);
        Assert.AreEqual(1, baseline.Length);
        StringAssert.Contains(baseline[0], fixture.PinA);
        StringAssert.Contains(baseline[0], fixture.PinB);
        StringAssert.Contains(baseline[0], fixture.Wire);
        var query = new GetItemsById { Header = new ItemHeader { Document = document } };
        query.Items.Add(new KIID { Value = fixture.Symbol });
        var symbol = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicSymbolInstance>();
        query.Items.Clear(); query.Items.Add(new KIID { Value = fixture.Wire });
        var wire = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicLine>();
        var moved = symbol.Clone(); moved.Position.XNm -= 5000000;
        moved.ReferenceField.Text.Position.XNm -= 5000000;
        moved.ValueField.Text.Position.XNm -= 5000000;
        var extended = wire.Clone(); extended.Start.XNm -= 5000000;
        Assert.AreEqual(symbol.Position, wire.Start, "Fixture wire must start at the moved pin.");
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Arrange connected probe" };
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(moved) });
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(extended) });
        var rejected = batch.Clone(); rejected.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        CollectionAssert.AreEqual(baseline, await Nets(), "Rejected electrical batches must restore the native graph.");
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
        var cursor = new ReadSchematicChangeJournal { Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence };
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var afterMove = await Nets();
        var electricalMove = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document }, token);
        Assert.IsTrue(electricalMove.Nets.Any(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == fixture.PinA)
            && n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == fixture.PinB)));
        var movedInSnapshot = electricalMove.Hierarchy.Data.Instances.SelectMany(s => s.Items)
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(s => s.Id.Value == fixture.Symbol);
        Assert.AreEqual(moved.Position, movedInSnapshot.Position);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-electrical-move-state.json"), SchematicJson.Formatter.Format(electricalMove), token);
        var electricalImage = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-connected-commit.png"), electricalImage.Png.ToByteArray(), token);
        query.Items.Clear(); query.Items.Add(new KIID { Value = fixture.Symbol });
        var actualSymbol = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicSymbolInstance>();
        query.Items.Clear(); query.Items.Add(new KIID { Value = fixture.Wire });
        var actualWire = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicLine>();
        Assert.AreEqual(moved.Position, actualSymbol.Position);
        Assert.AreEqual(extended.Start, actualWire.Start);
        var pin = actualSymbol.Definition.Items.Select(i => i.Item).Where(i => i.Is(SchematicPin.Descriptor))
            .Select(i => i.Unpack<SchematicPin>()).Single();
        Assert.AreEqual(new Vector2(), pin.Position, "Embedded definition pin must remain symbol-local.");
        Assert.IsTrue(baseline.SequenceEqual(afterMove),
            $"Coordinated movement changed nets: before={string.Join(";", baseline)} after={string.Join(";", afterMove)}");
        var committed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(1, committed.Changes.Count);
        cursor.AfterSequence = committed.Sequence;
        foreach (var (key, expected) in new[] { ("z", symbol.Position), ("y", moved.Position) })
        {
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicChangeJournal changed;
            do
            {
                changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                if (changed.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
            } while (changed.Changes.Count == 0);
            query.Items.Clear(); query.Items.Add(new KIID { Value = fixture.Symbol });
            Assert.AreEqual(expected, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicSymbolInstance>().Position);
            CollectionAssert.AreEqual(baseline, await Nets());
            cursor.AfterSequence = changed.Sequence;
        }
        var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-connected-redo.png"), image.Png.ToByteArray(), token);

        var header = new ItemHeader { Document = document };
        var staged = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, token);
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(netQuery, token))).Status);
        await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
        {
            Header = header, Id = staged.Id, Action = (CommitAction)2
        }, token);
        CollectionAssert.AreEqual(baseline, await Nets());

        // Legacy inputs still use sheet-space pins. This fixture has an identity
        // rotation, so its local origin translates directly to the anchor.
        var legacy = moved.Clone();
        legacy.Definition.PinsUseLocalCoordinates = false;
        foreach (var child in legacy.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
        {
            var legacyPin = child.Item.Unpack<SchematicPin>();
            legacyPin.Position.XNm += legacy.Position.XNm;
            legacyPin.Position.YNm += legacy.Position.YNm;
            child.Item = Any.Pack(legacyPin);
        }
        var legacyBatch = new ApplySchematicItemBatch { Document = document };
        legacyBatch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(legacy) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(legacyBatch, token);
        CollectionAssert.AreEqual(baseline, await Nets());

        // Precision guard: an actual disconnected move must change the graph.
        var disconnected = moved.Clone(); disconnected.Position.XNm -= 5000000;
        var split = new ApplySchematicItemBatch { Document = document };
        split.Operations.Add(new SchematicItemOperation { Update = Any.Pack(disconnected) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(split, token);
        var splitNets = await Nets();
        Assert.AreEqual(2, splitNets.Length);
        Assert.IsTrue(splitNets.Any(n => n.StartsWith("unconnected-", StringComparison.Ordinal) && n.Contains(fixture.PinA, StringComparison.Ordinal)));
        journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
        cursor.AfterSequence = journal.Sequence;
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        using var splitUndoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        splitUndoDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        SchematicChangeJournal restored;
        do
        {
            restored = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, splitUndoDeadline.Token);
            if (restored.Changes.Count == 0) await Task.Delay(100, splitUndoDeadline.Token);
        } while (restored.Changes.Count == 0);
        CollectionAssert.AreEqual(baseline, await Nets());
        await VerifyTransformedPins(client, document, moved, processId, display, evidence, instanceId, token);
        await VerifyMultiUnitDefinition(client, document, moved, processId, display, evidence, token);
    }
}
