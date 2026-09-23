using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<IReadOnlyList<SchematicPlacementGeometry>> MeasureCreationCandidates(NativeClient client,
        DocumentSpecifier root, CheckedSchematicState before, SchematicDesign proposed, SchematicDesign baseline,
        string evidence, string instanceId, CancellationToken token)
    {
        var oldIds = baseline.SymbolBindings.Select(b => b.SymbolOccurrenceId).ToHashSet();
        var newIds = proposed.SymbolBindings.Where(b => !oldIds.Contains(b.SymbolOccurrenceId))
            .Select(b => b.NativeObjectId.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var human = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root.Clone() }, token);
        var result = new List<SchematicPlacementGeometry>();
        foreach (var screen in proposed.Schematic.Instances)
        {
            var request = new MeasureSchematicPlacement { Document = screen.Metadata.Document.Clone(), ExpectedRevision = before.State.Revision.Clone() };
            request.Candidates.Add(screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => newIds.Contains(s.Id.Value)));
            var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
            Assert.AreEqual(request.Document, measured.Document); Assert.AreEqual(before.State.Revision, measured.Revision);
            Assert.AreEqual(screen.Metadata.ScreenId, measured.ScreenId);
            Assert.IsTrue(measured.PageBounds.Size.XNm > 0 && measured.PageBounds.Size.YNm > 0);
            Assert.IsTrue(measured.PinGeometryAvailable);
            var existingSymbols = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => !newIds.Contains(s.Id.Value))
                .ToDictionary(s => s.Id.Value);
            CollectionAssert.AreEquivalent(existingSymbols.Keys.ToArray(),
                measured.Obstacles.Where(o => o.SymbolPins is not null).Select(o => o.Id.Value).ToArray());
            foreach (var body in measured.Obstacles.Where(o => o.SymbolPins is not null))
            {
                Assert.IsTrue(body.SymbolPins.Complete);
                var symbol = existingSymbols[body.Id.Value];
                var expected = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                    .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
                    .Where(p => p.Pin.LibraryPinId is not null && ((p.Child.Unit?.Unit ?? 0) == 0 || p.Child.Unit!.Unit == symbol.Unit.Unit)
                        && ((p.Child.BodyStyle?.Style ?? 0) == 0 || p.Child.BodyStyle!.Style == (symbol.BodyStyle?.Style ?? 1)))
                    .ToDictionary(p => p.Pin.Id.Value);
                CollectionAssert.AreEquivalent(expected.Keys.ToArray(), body.SymbolPins.Pins.Select(p => p.Id.Value).ToArray());
                var matrix = SchematicOrientation.Geometry(new(0, 0, ((int)symbol.Transform.Orientation - 1) * 90,
                    symbol.Transform.MirrorX, symbol.Transform.MirrorY, false));
                foreach (var pin in body.SymbolPins.Pins)
                {
                    var source = expected[pin.Id.Value].Pin;
                    Assert.AreEqual(source.LibraryPinId, pin.LibraryPinId);
                    Assert.AreEqual(symbol.Position.XNm + matrix.Xx * source.Position.XNm + matrix.Xy * source.Position.YNm, pin.Position.XNm);
                    Assert.AreEqual(symbol.Position.YNm + matrix.Yx * source.Position.XNm + matrix.Yy * source.Position.YNm, pin.Position.YNm);
                }
            }
            CollectionAssert.AreEquivalent(request.Candidates.Select(c => c.Id.Value).ToArray(), measured.Candidates.Select(c => c.Id.Value).ToArray());
            foreach (var body in measured.Candidates)
            {
                Assert.IsTrue(body.Bounds.Size.XNm > 0 && body.Bounds.Size.YNm > 0);
                var symbol = request.Candidates.Single(s => s.Id.Equals(body.Id));
                Assert.IsNotNull(body.SymbolPins); Assert.IsTrue(body.SymbolPins.Complete);
                var expected = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                    .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
                    .Where(p => p.Pin.LibraryPinId is not null && ((p.Child.Unit?.Unit ?? 0) == 0 || p.Child.Unit!.Unit == symbol.Unit.Unit)
                        && ((p.Child.BodyStyle?.Style ?? 0) == 0 || p.Child.BodyStyle!.Style == (symbol.BodyStyle?.Style ?? 1)))
                    .ToDictionary(p => p.Pin.Id.Value);
                CollectionAssert.AreEquivalent(expected.Keys.ToArray(), body.SymbolPins.Pins.Select(p => p.Id.Value).ToArray());
                foreach (var pin in body.SymbolPins.Pins)
                {
                    var source = expected[pin.Id.Value];
                    Assert.AreEqual(source.Pin.LibraryPinId, pin.LibraryPinId);
                    Assert.AreEqual(source.Pin.Number, pin.Number);
                    Assert.AreEqual(string.IsNullOrEmpty(source.Pin.ActiveAlternate) ? source.Pin.Name : source.Pin.ActiveAlternate, pin.Name);
                    Assert.AreEqual(source.Child.Unit?.Unit ?? 0, pin.Unit);
                    Assert.AreEqual(source.Child.BodyStyle?.Style ?? 0, pin.BodyStyle);
                    Assert.AreEqual(source.Pin.Visible, pin.Visible);
                    Assert.AreEqual(1, Math.Abs(pin.BodyDirectionX) + Math.Abs(pin.BodyDirectionY));
                    Assert.AreEqual(0L, pin.Position.XNm % 100); Assert.AreEqual(0L, pin.Position.YNm % 100);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-measure-" + screen.Metadata.Document.SheetPath.Path[^1].Value + ".json"),
                SchematicJson.Formatter.Format(measured), token);
            var stale = request.Clone(); stale.ExpectedRevision.Sequence++;
            Assert.AreEqual(3, (await Assert.ThrowsAsync<NativeApiException>(() =>
                client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(stale, token))).Status);
            if (request.Candidates.Count > 0)
            {
                await VerifyPinTransforms(request);
                var duplicate = request.Clone(); duplicate.Candidates.Add(request.Candidates[0].Clone());
                Assert.AreEqual(3, (await Assert.ThrowsAsync<NativeApiException>(() =>
                    client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(duplicate, token))).Status);
                Action<MeasureSchematicPlacement>[] malformed =
                [
                    r => r.Candidates[0].Position = null,
                    r => r.Candidates[0].Position.XNm++,
                    r => r.Candidates[0].Position.XNm = ((long)int.MaxValue + 1) * 100,
                    r => r.Candidates[0].Unit.Unit = 0,
                    r => r.Candidates[0].Path.Path.Clear()
                ];
                foreach (var corrupt in malformed)
                {
                    var invalid = request.Clone(); corrupt(invalid);
                    Assert.AreEqual(3, (await Assert.ThrowsAsync<NativeApiException>(() =>
                        client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(invalid, token))).Status);
                }
                byte[] unknownBytes = [.. request.ToByteArray(), 0xf8, 0x3e, 0x01];
                var unknown = MeasureSchematicPlacement.Parser.ParseFrom(unknownBytes);
                Assert.AreEqual(3, (await Assert.ThrowsAsync<NativeApiException>(() =>
                    client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(unknown, token))).Status);
                // Label prototypes are declared for connected realization (contract CN-1) but not measured yet:
                // they fail closed exactly as the unknown field did before the declaration.
                var prototype = request.Clone();
                prototype.ItemCandidates.Add(Any.Pack(new LocalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
                    Position = request.Candidates[0].Position.Clone(), Text = new() { Text_ = "PROBE" } }));
                var unmeasured = await Assert.ThrowsAsync<NativeApiException>(() =>
                    client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(prototype, token));
                Assert.AreEqual(3, unmeasured.Status);
                Assert.AreEqual("Placement measurement contains unsupported fields", unmeasured.Message);
            }
            result.Add(measured);
            await VerifyMcpGeometry(client, instanceId, request, measured, evidence, token);
        }
        Assert.AreEqual(human, await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root.Clone() }, token));
        Assert.AreEqual(before, await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token));
        return result;

        async Task VerifyPinTransforms(MeasureSchematicPlacement original)
        {
            var request = original.Clone();
            var symbol = request.Candidates[0].Clone(); request.Candidates.Clear(); request.Candidates.Add(symbol);
            symbol.Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 };
            var neutral = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
            var basePins = neutral.Candidates.Single().SymbolPins.Pins.ToDictionary(p => p.Id.Value);
            foreach (int angle in new[] { 0, 90, 180, 270 })
            foreach (bool mirrorX in new[] { false, true })
            foreach (bool mirrorY in new[] { false, true })
            {
                symbol.Transform = new() { Orientation = (SchematicSymbolOrientation)(angle / 90 + 1),
                    MirrorX = mirrorX, MirrorY = mirrorY };
                var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                var matrix = SchematicOrientation.Geometry(new(0, 0, angle, mirrorX, mirrorY, false));
                foreach (var pin in measured.Candidates.Single().SymbolPins.Pins)
                {
                    var originalPin = basePins[pin.Id.Value];
                    long x = originalPin.Position.XNm - symbol.Position.XNm;
                    long y = originalPin.Position.YNm - symbol.Position.YNm;
                    Assert.AreEqual(symbol.Position.XNm + matrix.Xx * x + matrix.Xy * y, pin.Position.XNm);
                    Assert.AreEqual(symbol.Position.YNm + matrix.Yx * x + matrix.Yy * y, pin.Position.YNm);
                    Assert.AreEqual(matrix.Xx * originalPin.BodyDirectionX + matrix.Xy * originalPin.BodyDirectionY, pin.BodyDirectionX);
                    Assert.AreEqual(matrix.Yx * originalPin.BodyDirectionX + matrix.Yy * originalPin.BodyDirectionY, pin.BodyDirectionY);
                }
            }
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>(); pin.Visible = false; child.Item = Any.Pack(pin);
            }
            var hidden = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
            Assert.AreEqual(basePins.Count, hidden.Candidates.Single().SymbolPins.Pins.Count,
                "Hidden active pins are still electrical connection points.");
            Assert.IsTrue(hidden.Candidates.Single().SymbolPins.Pins.All(p => !p.Visible));
        }
    }

    private static async Task VerifyCreatedGeometry(NativeClient client, DocumentSpecifier root,
        IReadOnlyList<SchematicPlacementGeometry> proposed, CancellationToken token)
    {
        foreach (var sheet in proposed)
        {
            // Independent existing-object bounds use the displayed instance.
            // Navigation here is an explicit test action after isolation proof.
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = sheet.Document.Clone() }, token);
            var query = new GetBoundingBox { Header = new() { Document = sheet.Document.Clone() }, Mode = BoundingBoxMode.BbmItemAndChildText };
            query.Items.Add(sheet.Candidates.Select(c => c.Id.Clone()));
            var actual = await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(query, token);
            for (int i = 0; i < actual.Items.Count; i++)
                Assert.AreEqual(sheet.Candidates.Single(c => c.Id.Equals(actual.Items[i])).Bounds, actual.Boxes[i],
                    "A detached native proposal must measure the same complete body/field envelope as the created symbol.");
            foreach (var proposal in sheet.Candidates)
            {
                // The existing item query exposes pins through their owning symbol,
                // not as independently addressable top-level schematic objects.
                var symbols = new GetItemsById { Header = new() { Document = sheet.Document.Clone() } };
                symbols.Items.Add(proposal.Id.Clone());
                var observed = await client.InvokeAsync<GetItemsById, GetItemsResponse>(symbols, token);
                var nativeSymbol = observed.Items.Single().Unpack<SchematicSymbolInstance>();
                Assert.AreEqual(proposal.Id, nativeSymbol.Id);
                var nativePins = nativeSymbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                    .Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.LibraryPinId is not null)
                    .ToDictionary(p => p.Id.Value);
                Assert.IsTrue(nativeSymbol.Definition.PinsUseLocalCoordinates);
                var matrix = SchematicOrientation.Geometry(new(0, 0,
                    ((int)nativeSymbol.Transform.Orientation - 1) * 90,
                    nativeSymbol.Transform.MirrorX, nativeSymbol.Transform.MirrorY, false));
                foreach (var pin in proposal.SymbolPins.Pins)
                {
                    // Native snapshot pins use unrotated symbol-local coordinates,
                    // not the Y-up coordinates of the on-disk symbol grammar.
                    var local = nativePins[pin.Id.Value].Position;
                    Assert.AreEqual(nativeSymbol.Position.XNm + matrix.Xx * local.XNm + matrix.Xy * local.YNm, pin.Position.XNm);
                    Assert.AreEqual(nativeSymbol.Position.YNm + matrix.Yx * local.XNm + matrix.Yy * local.YNm, pin.Position.YNm);
                    Assert.AreEqual(pin.LibraryPinId, nativePins[pin.Id.Value].LibraryPinId);
                }
            }
        }
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
    }
}
