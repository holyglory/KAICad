using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Client;

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
        var mcp = new List<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Expected)>();
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
                Assert.AreEqual(SchematicPinGeometryIncompleteReason.SpgirUnspecified, body.SymbolPins.IncompleteReason);
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
                    RequirePowerFacts(symbol, source, pin);
                }
            }
            CollectionAssert.AreEquivalent(request.Candidates.Select(c => c.Id.Value).ToArray(), measured.Candidates.Select(c => c.Id.Value).ToArray());
            foreach (var body in measured.Candidates)
            {
                Assert.IsTrue(body.Bounds.Size.XNm > 0 && body.Bounds.Size.YNm > 0);
                var symbol = request.Candidates.Single(s => s.Id.Equals(body.Id));
                Assert.IsNotNull(body.SymbolPins); Assert.IsTrue(body.SymbolPins.Complete);
                Assert.AreEqual(SchematicPinGeometryIncompleteReason.SpgirUnspecified, body.SymbolPins.IncompleteReason);
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
                    RequirePowerFacts(symbol, source.Pin, pin);
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
                await VerifyPowerProbes(client, request, token);
                mcp.Add(await VerifyLabelPrototypes(client, request, measured, before, evidence, instanceId, token));
            }
            result.Add(measured);
            mcp.Add((request, measured));
        }
        await VerifyMcpGeometries(client, instanceId, mcp, evidence, token);
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

    // The implicit power connection native reports for a placed pin must be the one its captured definition implies
    // (cn1-wiring-intent.md §6.2): global and local power symbols join the net of their value, a hidden power input
    // on an ordinary symbol joins the global net of its shown name, and every other pin joins nothing implicitly.
    // The electrical type is the active alternate's when one is selected.
    private static void RequirePowerFacts(SchematicSymbolInstance symbol, SchematicPin source, SchematicPinAnchor measured)
    {
        bool alternate = source.HasActiveAlternate && source.ActiveAlternate.Length != 0;
        var type = alternate ? source.Alternates.Single(a => a.Name == source.ActiveAlternate).ElectricalType : source.ElectricalType;
        Assert.AreEqual(type, measured.ElectricalType, "Pin " + source.Number + " must report its effective electrical type.");
        var (scope, name) = type != ElectricalPinType.EptPowerInput ? (SchematicPinPowerScope.SppsNone, "")
            : symbol.Definition.Type == SchematicSymbolType.SstGlobalPower ? (SchematicPinPowerScope.SppsGlobal, symbol.ValueField.Text.Text_)
            : symbol.Definition.Type == SchematicSymbolType.SstLocalPower ? (SchematicPinPowerScope.SppsLocal, symbol.ValueField.Text.Text_)
            : source.Visible ? (SchematicPinPowerScope.SppsNone, "") : (SchematicPinPowerScope.SppsGlobal, alternate ? source.ActiveAlternate : source.Name);
        Assert.AreEqual(scope, measured.PowerScope, "Pin " + source.Number + " power scope.");
        Assert.AreEqual(name, measured.PowerNet, "Pin " + source.Number + " power net.");
    }

    // Detached variants of a real candidate (a global power symbol, a local power symbol, and an ordinary symbol with
    // one hidden and otherwise visible power inputs) must report exactly the implicit connections KiCad gives them.
    private static async Task VerifyPowerProbes(NativeClient client, MeasureSchematicPlacement original, CancellationToken token)
    {
        var request = original.Clone(); request.Candidates.Clear();
        SchematicSymbolInstance Variant(SchematicSymbolType type, string value, Func<int, bool> hidden)
        {
            var symbol = original.Candidates[0].Clone();
            symbol.Id = new() { Value = Guid.NewGuid().ToString("D") };
            symbol.Definition.Type = type;
            symbol.ValueField.Text.Text_ = value;
            int index = 0;
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                pin.ElectricalType = ElectricalPinType.EptPowerInput; pin.Visible = !hidden(index++); pin.Name = "PROBE_POWER_PIN";
                child.Item = Any.Pack(pin);
            }
            return symbol;
        }
        var variants = new (SchematicSymbolInstance Symbol, Func<int, SchematicPinPowerScope> Scope, Func<int, string> Name)[]
        {
            (Variant(SchematicSymbolType.SstGlobalPower, "PROBE_GLOBAL", _ => true), _ => SchematicPinPowerScope.SppsGlobal, _ => "PROBE_GLOBAL"),
            (Variant(SchematicSymbolType.SstLocalPower, "PROBE_LOCAL", _ => true), _ => SchematicPinPowerScope.SppsLocal, _ => "PROBE_LOCAL"),
            (Variant(SchematicSymbolType.SstNormal, "PROBE_NORMAL", i => i == 0), i => i == 0 ? SchematicPinPowerScope.SppsGlobal : SchematicPinPowerScope.SppsNone,
                i => i == 0 ? "PROBE_POWER_PIN" : "")
        };
        request.Candidates.Add(variants.Select(v => v.Symbol));
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
        foreach (var (symbol, scope, name) in variants)
        {
            var body = measured.Candidates.Single(c => c.Id.Equals(symbol.Id));
            Assert.IsTrue(body.SymbolPins.Complete);
            var order = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).Select(c => c.Item.Unpack<SchematicPin>().Id.Value).ToList();
            Assert.IsNotEmpty(body.SymbolPins.Pins);
            foreach (var pin in body.SymbolPins.Pins)
            {
                int index = order.IndexOf(pin.Id.Value);
                Assert.AreEqual(ElectricalPinType.EptPowerInput, pin.ElectricalType);
                Assert.AreEqual(scope(index), pin.PowerScope, symbol.Definition.Type + " pin " + pin.Number);
                Assert.AreEqual(name(index), pin.PowerNet, symbol.Definition.Type + " pin " + pin.Number);
            }
        }
    }

    // Label prototypes for connected realization (cn1-wiring-intent.md §6.2 round 2), with the exact payloads the
    // realizer generates: every kind in every direction is measured detached, reported in request order at its own
    // anchor, faces away from that anchor within the orientation tolerance, and measures the same envelope anywhere.
    // Invalid prototypes fail closed. Returns the measured request for the MCP parity check.
    private static async Task<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Expected)> VerifyLabelPrototypes(NativeClient client,
        MeasureSchematicPlacement original, SchematicPlacementGeometry symbols, CheckedSchematicState before, string evidence, string instanceId,
        CancellationToken token)
    {
        var policy = SchematicConnectionPolicy.FromSnapshot(before.Electrical.Hierarchy.Data);
        var revision = new KiCad.Automation.Model.DocumentRevision(before.State.Revision.Epoch, before.State.Revision.Sequence);
        Guid screen = Guid.Parse(symbols.ScreenId.Value);
        var origin = original.Candidates[0].Position;
        var kinds = new[] { ConnectionLabelKind.Local, ConnectionLabelKind.Global, ConnectionLabelKind.Hierarchical };
        Assert.AreEqual(1_270_000L, policy.TextSizeNm, "The realizer tests' label back extents are measured with 1.27 mm text.");
        var spins = new[] { SchematicLabelSpinStyle.SlssRight, SchematicLabelSpinStyle.SlssLeft, SchematicLabelSpinStyle.SlssUp, SchematicLabelSpinStyle.SlssBottom };
        MeasureSchematicPlacement Prototypes(long dx, long dy)
        {
            var request = original.Clone(); request.Candidates.Clear();
            foreach (var kind in kinds)
            foreach (var spin in spins)
            {
                var shape = kind == ConnectionLabelKind.Local ? SchematicLabelShape.SlshUnknown : SchematicLabelShape.SlshPassive;
                var id = SchematicConnectionIdentity.Probe(revision, screen, SchematicConnectionRealizer.Descriptor(kind), "PROBE_NET", spin, shape);
                request.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(kind, id,
                    new() { XNm = origin.XNm + dx, YNm = origin.YNm + dy }, "PROBE_NET", spin, policy)));
            }
            return request;
        }
        var first = Prototypes(0, 0);
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(first, token);
        Assert.AreEqual(first.Document, measured.Document); Assert.AreEqual(first.ExpectedRevision, measured.Revision);
        Assert.AreEqual(symbols.ScreenId, measured.ScreenId);
        Assert.IsEmpty(measured.Candidates);
        Assert.AreEqual(symbols.Obstacles, measured.Obstacles, "One revision measures the same existing items with or without prototypes.");
        Assert.HasCount(first.ItemCandidates.Count, measured.ItemCandidates);
        var relative = new List<(long L, long T, long R, long B)>();
        for (int i = 0; i < first.ItemCandidates.Count; i++)
        {
            var prototype = SchematicItemDelta.Index([first.ItemCandidates[i]]).Single();
            var position = (Vector2)prototype.Value.Descriptor.FindFieldByName("position").Accessor.GetValue(prototype.Value);
            var body = measured.ItemCandidates[i];
            Assert.AreEqual(prototype.Key.ToString("D"), body.Id.Value, "Prototypes are reported in request order.");
            Assert.AreEqual(position, body.Anchor);
            Assert.IsNull(body.SymbolPins);
            Assert.IsTrue(body.Bounds.Size.XNm > 0 && body.Bounds.Size.YNm > 0);
            var box = (L: body.Bounds.Position.XNm - position.XNm, T: body.Bounds.Position.YNm - position.YNm,
                R: body.Bounds.Position.XNm + body.Bounds.Size.XNm - position.XNm, B: body.Bounds.Position.YNm + body.Bounds.Size.YNm - position.YNm);
            relative.Add(box);
            var spin = spins[i % spins.Length];
            var (fx, fy) = SchematicConnectionRealizer.Facing(spin);
            long behind = fx > 0 ? -box.L : fx < 0 ? box.R : fy > 0 ? -box.T : box.B;
            long ahead = fx > 0 ? box.R : fx < 0 ? -box.L : fy > 0 ? box.B : -box.T;
            Assert.IsLessThanOrEqualTo(policy.LabelBackToleranceNm, behind, kinds[i / spins.Length] + " " + spin + " reaches too far behind its anchor.");
            // The realizer tests draw every label kind exactly as far behind its anchor as KiCad does here.
            Assert.AreEqual(SchematicConnectionRealizerTests.Geometry.MeasuredBehind[prototype.Value.GetType()], behind,
                kinds[i / spins.Length] + " " + spin + " back extent differs from the one the realizer tests draw.");
            Assert.IsGreaterThan(policy.TextSizeNm, ahead, kinds[i / spins.Length] + " " + spin + " must extend in the direction it faces.");
        }
        // Local, global and hierarchical labels of one text are measured with their own shapes.
        Assert.IsTrue(relative[spins.Length] != relative[0] && relative[2 * spins.Length] != relative[0]);
        // Translation invariance: the same prototypes measured four grids right and two down have identical envelopes.
        var moved = Prototypes(4 * policy.GridNm, 2 * policy.GridNm);
        var shifted = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(moved, token);
        for (int i = 0; i < moved.ItemCandidates.Count; i++)
        {
            var body = shifted.ItemCandidates[i];
            Assert.AreEqual(relative[i], (body.Bounds.Position.XNm - body.Anchor.XNm, body.Bounds.Position.YNm - body.Anchor.YNm,
                body.Bounds.Position.XNm + body.Bounds.Size.XNm - body.Anchor.XNm, body.Bounds.Position.YNm + body.Bounds.Size.YNm - body.Anchor.YNm));
        }
        await VerifyAnchorLabels(client, original, symbols, before, revision, screen, policy, kinds, evidence, instanceId, token);
        // Symbols and prototypes measure together, sharing the request's limit and identities.
        var combined = original.Clone(); combined.ItemCandidates.Add(first.ItemCandidates);
        var both = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(combined, token);
        Assert.AreEqual(symbols.Candidates, both.Candidates);
        Assert.AreEqual(measured.ItemCandidates, both.ItemCandidates);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-measure-prototypes-" + original.Document.SheetPath.Path[^1].Value + ".json"),
            SchematicJson.Formatter.Format(measured), token);
        // Invalid prototypes fail closed without measuring anything.
        async Task Refused(string problem, Action<MeasureSchematicPlacement> corrupt, string? message = null)
        {
            var invalid = first.Clone(); corrupt(invalid);
            var error = await Assert.ThrowsAsync<NativeApiException>(() => client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(invalid, token), problem);
            Assert.AreEqual(3, error.Status, problem);
            if (message is not null) Assert.AreEqual(message, error.Message, problem);
        }
        LocalLabel First(MeasureSchematicPlacement request) => request.ItemCandidates[0].Unpack<LocalLabel>();
        void Replace(MeasureSchematicPlacement request, LocalLabel label) => request.ItemCandidates[0] = Any.Pack(label);
        await Refused("a repeated prototype identity", r => r.ItemCandidates.Add(r.ItemCandidates[0].Clone()));
        await Refused("an identity already in the document", r => { var l = First(r); l.Id = symbols.Obstacles[0].Id.Clone(); Replace(r, l); });
        await Refused("an identity of a symbol candidate in the same request", r =>
        {
            r.Candidates.Add(original.Candidates[0].Clone()); var l = First(r); l.Id = original.Candidates[0].Id.Clone(); Replace(r, l);
        });
        await Refused("a non-canonical identity", r => { var l = First(r); l.Id.Value = l.Id.Value.ToUpperInvariant(); Replace(r, l); });
        await Refused("a position off the 100 nm quantum", r => { var l = First(r); l.Position.XNm += 1; Replace(r, l); });
        await Refused("no position", r => { var l = First(r); l.Position = null; Replace(r, l); });
        await Refused("multiline text", r => { var l = First(r); l.Text.Attributes.Multiline = true; Replace(r, l); });
        await Refused("a line break", r => { var l = First(r); l.Text.Text_ = "PROBE\nNET"; Replace(r, l); });
        await Refused("a directive label", r => r.ItemCandidates.Add(Any.Pack(new DirectiveLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = origin.Clone(), Text = new() { Text_ = "PROBE_NET", Attributes = new() { Multiline = false } } })),
            "Item candidates must be local, global or hierarchical label prototypes");
        await Refused("a wire", r => r.ItemCandidates.Add(Any.Pack(new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = origin.Clone(), End = origin.Clone(), Type = SchematicLineType.SltWire })), "Item candidates must be local, global or hierarchical label prototypes");
        await Refused("an unknown field inside a prototype", r => r.ItemCandidates[0] = new Any { TypeUrl = r.ItemCandidates[0].TypeUrl,
            Value = ByteString.CopyFrom([.. r.ItemCandidates[0].Value.ToByteArray(), 0xf8, 0x3e, 0x01]) }, "Placement measurement contains unsupported fields");
        await Refused("more than 256 candidates and prototypes", r =>
        {
            var template = First(r);
            for (int i = r.ItemCandidates.Count; i <= 256; i++)
            {
                var extra = template.Clone(); extra.Id = new() { Value = Guid.NewGuid().ToString("D") }; r.ItemCandidates.Add(Any.Pack(extra));
            }
        }, "Measure at most 256 symbol and item candidates together per request");
        return (first, measured);
    }

    // Labels on pins themselves (the anchor label that names an existing connection, cn1-wiring-intent.md §6.3 (a)) on
    // this real sheet. A label of every kind is measured on every visible pin of every symbol whose pins KiCad reports
    // completely, and the realizer's own §6.4 rule decides on the whole measured sheet whether it could name an existing
    // connection there; the verdict, and the rule that refuses it, is recorded for each pin and kind, with the number of
    // symbols skipped. Every label reaches behind its anchor over its own pin, and KiCad's bounds of a symbol reach past
    // each visible pin by the target it draws on an unconnected pin end (PinTargetReachNm), so a label on a pin always
    // overlaps its own symbol. The rule must still admit a label of each kind on this sheet, and may refuse one because
    // of its own symbol only where that symbol draws more than its pin target in front of the pin. On a pin that already
    // has wires or labels, the same label must be refused once those are treated as another connection's items; the
    // recorded count takes only labels the rule admitted before, and the root sheet (with its wired probe link) needs one.
    // The creation journey's new symbols all face one way, so the first one is also judged turned to every other
    // orientation on the same sheet, and labels are judged facing all four ways. Must-catch on the same real geometry: a
    // new symbol whose own reference text is moved in front of a pin, where a label admitted there runs, draws beyond
    // its pin target and refuses that label; the root sheet must run it.
    private static async Task VerifyAnchorLabels(NativeClient client, MeasureSchematicPlacement original, SchematicPlacementGeometry symbols,
        CheckedSchematicState before, KiCad.Automation.Model.DocumentRevision revision, Guid screen, SchematicConnectionPolicy policy,
        ConnectionLabelKind[] kinds, string evidence, string instanceId, CancellationToken token)
    {
        static (long L, long T, long R, long B) Box(Box2 b) => (b.Position.XNm, b.Position.YNm, b.Position.XNm + b.Size.XNm, b.Position.YNm + b.Size.YNm);
        static bool Overlap((long L, long T, long R, long B) a, (long L, long T, long R, long B) b) =>
            a.L < a.R && a.T < a.B && b.L < b.R && b.T < b.B && a.L < b.R && b.L < a.R && a.T < b.B && b.T < a.B;
        var native = before.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(original.Document));
        // Each pin's existing connection on this sheet, as KiCad reports it; an unconnected pin is alone.
        var connection = new Dictionary<string, Guid[]>(StringComparer.Ordinal);
        foreach (var net in before.Electrical.Nets)
            foreach (var sheet in net.Sheets.Where(s => s.Path.Equals(original.Document.SheetPath)))
            {
                var items = sheet.Items.Select(i => Guid.Parse(i.Value)).ToArray();
                foreach (var item in sheet.Items) connection[item.Value] = items;
            }
        var measuredSymbols = symbols.Obstacles.Concat(symbols.Candidates).Where(o => o.SymbolPins is not null).ToArray();
        var owners = measuredSymbols.Where(o => o.SymbolPins.Complete && o.SymbolPins.Pins.Any(p => p.Visible)).OrderBy(o => o.Id.Value, StringComparer.Ordinal).ToArray();
        int skipped = measuredSymbols.Length - owners.Length;
        (SchematicPlacementBounds Owner, SchematicPinAnchor Pin, ConnectionLabelKind Kind)[] Visible(SchematicPlacementBounds owner) =>
            [.. owner.SymbolPins.Pins.Where(p => p.Visible).OrderBy(p => p.Id.Value, StringComparer.Ordinal).SelectMany(p => kinds.Select(k => (owner, p, k)))];
        var probes = owners.SelectMany(Visible).ToArray();
        Assert.IsNotEmpty(probes, "This sheet has visible pins to check.");

        // Measure a label of each listed kind on each listed pin, check what KiCad measures, and judge it with the realizer's
        // own rule against `sheet`: this sheet measured with its new symbols (possibly turned or edited).
        async Task<List<AnchorLabelVerdict>> Judge(SchematicPlacementGeometry sheet,
            IReadOnlyList<(SchematicPlacementBounds Owner, SchematicPinAnchor Pin, ConnectionLabelKind Kind)> listed)
        {
            var verdicts = new List<AnchorLabelVerdict>();
            foreach (var chunk in listed.Chunk(SchematicConnectionRealizer.MaxMeasuredCandidates))
            {
                var request = original.Clone(); request.Candidates.Clear();
                foreach (var (_, pin, kind) in chunk)
                {
                    var spin = SchematicConnectionGeometry.Spin(SchematicConnectionGeometry.Outward(pin));
                    var shape = kind == ConnectionLabelKind.Local ? SchematicLabelShape.SlshUnknown : SchematicLabelShape.SlshPassive;
                    // One probe identity per pin: the symbol-probe form keyed by the placed pin.
                    var id = SchematicConnectionIdentity.Probe(revision, screen, SchematicConnectionRealizer.Descriptor(kind), "PROBE_ANCHOR", spin, shape,
                        Guid.Parse(pin.Id.Value), 0);
                    request.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(kind, id, pin.Position.Clone(), "PROBE_ANCHOR", spin, policy)));
                }
                var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                Assert.HasCount(request.ItemCandidates.Count, measured.ItemCandidates);
                Assert.AreEqual(symbols.Obstacles, measured.Obstacles, "One revision measures the same existing items with or without anchor labels.");
                for (int i = 0; i < chunk.Length; i++)
                {
                    var (owner, pin, kind) = chunk[i];
                    var outward = SchematicConnectionGeometry.Outward(pin);
                    string what = kind + " label on pin " + pin.Number + " of " + owner.Id.Value + " facing (" + outward.Dx + "," + outward.Dy + ")";
                    var label = Box(measured.ItemCandidates[i].Bounds);
                    var body = Box(owner.Bounds);
                    Assert.AreEqual(pin.Position, measured.ItemCandidates[i].Anchor, what);
                    long reach = outward switch
                    {
                        (-1, 0) => pin.Position.XNm - body.L, (1, 0) => body.R - pin.Position.XNm,
                        (0, -1) => pin.Position.YNm - body.T, _ => body.B - pin.Position.YNm
                    };
                    long behind = outward switch { (-1, 0) => label.R - pin.Position.XNm, (1, 0) => pin.Position.XNm - label.L,
                        (0, -1) => label.B - pin.Position.YNm, _ => pin.Position.YNm - label.T };
                    // KiCad's bounds include every visible pin up to its end and, on a pin that can be left unconnected, the target
                    // drawn around that end.
                    bool target = pin.ElectricalType is not (ElectricalPinType.EptNoConnect or ElectricalPinType.EptFree);
                    Assert.IsGreaterThanOrEqualTo(target ? SchematicConnectionRealizer.PinTargetReachNm : 0L, reach, what + ": the symbol's bounds past the pin.");
                    Assert.AreEqual(SchematicConnectionRealizerTests.Geometry.MeasuredBehind[SchematicConnectionRealizer.Descriptor(kind).ClrType], behind,
                        what + " reaches behind its anchor as the realizer tests draw it.");
                    Assert.IsTrue(Overlap(label, body), what + " overlaps its own symbol's bounds at the pin.");
                    var own = connection.GetValueOrDefault(pin.Id.Value) ?? [];
                    var refusal = SchematicConnectionRealizer.AnchorLabelRefusal(native, sheet, Guid.Parse(owner.Id.Value), pin, measured.ItemCandidates[i].Bounds,
                        own, policy);
                    if (refusal is not null && refusal.StartsWith("the label overlaps symbol " + owner.Id.Value, StringComparison.Ordinal))
                        Assert.IsGreaterThan(SchematicConnectionRealizer.PinTargetReachNm, reach,
                            what + " is refused by its own symbol only where that symbol draws beyond its pin target: " + refusal);
                    verdicts.Add(new(owner, pin, kind, outward, reach, behind, refusal, own, measured.ItemCandidates[i].Bounds));
                }
            }
            return verdicts;
        }

        var facts = new List<object>();
        var admitted = kinds.ToDictionary(k => k, _ => 0);
        int refusedAsForeign = 0;
        var verdicts = await Judge(symbols, probes);
        foreach (var verdict in verdicts)
        {
            var (owner, pin, kind) = (verdict.Owner, verdict.Pin, verdict.Kind);
            if (verdict.Refusal is null) admitted[kind]++;
            // Must-catch on the same real geometry: for a pin that already has wires or labels, the same label is refused
            // once those items are not taken as the pin's own connection (they would touch or run through it). Only a label
            // the rule admits with its own connection shows that treating those items as another connection's refuses it.
            string? foreign = verdict.Connection.Length > 1 ? SchematicConnectionRealizer.AnchorLabelRefusal(native, symbols, Guid.Parse(owner.Id.Value), pin,
                verdict.Label, [], policy) : null;
            if (verdict.Connection.Length > 1)
            {
                Assert.IsNotNull(foreign, kind + " label on pin " + pin.Number + " of " + owner.Id.Value
                    + " would touch the items of its pin's connection if they belonged to another one.");
                if (verdict.Refusal is null) refusedAsForeign++;
            }
            facts.Add(new { owner = owner.Id.Value, pin = pin.Number, kind = kind.ToString(), outward = new[] { verdict.Outward.Dx, verdict.Outward.Dy },
                reach = verdict.Reach, behind = verdict.Behind, admitted = verdict.Refusal is null, whyNot = verdict.Refusal, connectionItems = verdict.Connection.Length,
                whyNotIfForeign = foreign });
        }
        foreach (var kind in kinds)
            Assert.IsGreaterThan(0, admitted[kind], "The realizer's rule admits a " + kind + " label on some pin of this real sheet.");
        // The root sheet always holds the wired probe link, so the must-catch above must actually have run there.
        bool root = original.Document.SheetPath.Path.Count == 1;
        if (root)
            Assert.IsGreaterThan(0, refusedAsForeign, "A label admitted on a wired pin of the root sheet is refused once its wires are another connection's.");

        // The first new symbol turned about its own anchor to each other orientation, on the same sheet, judged by the same
        // rule and checks: with the untouched sheet, labels on pins facing all four ways.
        var first = original.Candidates[0];
        var turned = new List<AnchorLabelVerdict>();
        foreach (var orientation in Enumerable.Range(0, 4).Select(quarter => (SchematicSymbolOrientation)(quarter + 1)))
        {
            if (first.Transform is { MirrorX: false, MirrorY: false } transform && transform.Orientation == orientation) continue;
            var request = original.Clone();
            request.Candidates[0].Transform = new() { Orientation = orientation };
            var sheet = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
            Assert.AreEqual(symbols.Obstacles, sheet.Obstacles, "Turning a new symbol measures the same existing items.");
            turned.AddRange(await Judge(sheet, Visible(sheet.Candidates.Single(c => c.Id.Equals(first.Id)))));
        }
        CollectionAssert.AreEquivalent(new[] { (1, 0), (-1, 0), (0, 1), (0, -1) },
            verdicts.Concat(turned).Select(v => v.Outward).Distinct().ToArray(), "Anchor labels are judged on pins facing every way.");

        // Must-catch: a new symbol's own reference text moved three grids in front of a pin whose local label the rule admits.
        // Only that symbol's bounds change, so the label is now refused by its own symbol, beyond its pin target.
        object? fieldInFront = null;
        var basis = verdicts.FirstOrDefault(v => v.Refusal is null && v.Kind == ConnectionLabelKind.Local && symbols.Candidates.Any(c => c.Id.Equals(v.Owner.Id)));
        if (root) Assert.IsNotNull(basis, "A local label on a new symbol's pin is admitted on the root sheet.");
        if (basis is not null)
        {
            var request = original.Clone();
            var symbol = request.Candidates.Single(c => c.Id.Equals(basis.Owner.Id));
            long ahead = 3 * policy.GridNm;
            symbol.ReferenceField.Visible = true;
            symbol.ReferenceField.Text.Position = new() { XNm = basis.Pin.Position.XNm + basis.Outward.Dx * ahead, YNm = basis.Pin.Position.YNm + basis.Outward.Dy * ahead };
            var sheet = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
            Assert.AreEqual(symbols.Obstacles, sheet.Obstacles, "Moving a new symbol's text measures the same existing items.");
            var moved = sheet.Candidates.Single(c => c.Id.Equals(symbol.Id));
            var refused = (await Judge(sheet, [(moved, moved.SymbolPins.Pins.Single(p => p.Id.Equals(basis.Pin.Id)), ConnectionLabelKind.Local)])).Single();
            Assert.IsGreaterThan(SchematicConnectionRealizer.PinTargetReachNm, refused.Reach, "The moved reference text is drawn in front of the pin.");
            Assert.AreEqual("the label overlaps symbol " + moved.Id.Value + " more than the pin target in front of the pin", refused.Refusal,
                "A label its own symbol's text overlaps beyond the pin target is refused by that symbol.");
            fieldInFront = new { owner = moved.Id.Value, pin = basis.Pin.Number, reachBefore = basis.Reach, reachAfter = refused.Reach, whyNot = refused.Refusal };
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-anchor-labels-" + original.Document.SheetPath.Path[^1].Value + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { skippedSymbols = skipped, checkedSymbols = owners.Length, checkedPins = probes.Length / kinds.Length,
                admitted = admitted.ToDictionary(p => p.Key.ToString(), p => p.Value), refusedAsForeign, facts,
                turned = turned.Select(v => new { owner = v.Owner.Id.Value, pin = v.Pin.Number, kind = v.Kind.ToString(), outward = new[] { v.Outward.Dx, v.Outward.Dy },
                    reach = v.Reach, behind = v.Behind, admitted = v.Refusal is null, whyNot = v.Refusal }).ToArray(),
                fieldInFront }), token);
    }

    // One anchor label judged on a live sheet: its pin and owner as measured, the pin's outward direction, how far the owner's
    // bounds reach past the pin, how far the label reaches behind it, the realizer's verdict, the pin's existing connection
    // on the sheet and the label's measured bounds.
    private sealed record AnchorLabelVerdict(SchematicPlacementBounds Owner, SchematicPinAnchor Pin, ConnectionLabelKind Kind, (int Dx, int Dy) Outward,
        long Reach, long Behind, string? Refusal, Guid[] Connection, Box2 Label);

    // The public MCP measurement tool must return exactly the native geometry for every measured
    // sheet instance, reject a stale revision and replay the same answer. One service session serves
    // all instances, so the check does not pay a process start and shutdown per sheet.
    private static async Task VerifyMcpGeometries(NativeClient native, string instanceId,
        IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Expected)> measured,
        string evidence, CancellationToken token)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string state = Directory.CreateTempSubdirectory("kicad-mcp-geometry-").FullName;
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin",
                    configuration, "net10.0", "kicad-mcp.dll")],
                EnvironmentVariables = new Dictionary<string, string?> { ["KICAD_AUTOMATION_STATE_DIRECTORY"] = state }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: token);
            var attached = await client.CallToolAsync("kicad_instance_attach", new Dictionary<string, object?>
                { ["endpoint"] = native.Endpoint, ["expectedInstanceId"] = instanceId }, cancellationToken: token);
            Assert.IsFalse(attached.IsError == true);
            async Task<ModelContextProtocol.Protocol.CallToolResult> Read(MeasureSchematicPlacement query) =>
                await client.CallToolAsync("kicad_schematic_measure_placement", new Dictionary<string, object?>
                    { ["instanceId"] = instanceId, ["requestJson"] = SchematicJson.Formatter.Format(query) }, cancellationToken: token);
            foreach (var (request, expected) in measured)
            {
                var observed = await Read(request);
                Assert.IsFalse(observed.IsError == true);
                Assert.AreEqual(expected, SchematicJson.Parser.Parse<SchematicPlacementGeometry>(
                    observed.StructuredContent!.Value.GetProperty("geometry").GetRawText()));
                var stale = request.Clone(); stale.ExpectedRevision.Sequence++;
                Assert.IsTrue((await Read(stale)).IsError == true);
                var recovered = await Read(request);
                Assert.IsFalse(recovered.IsError == true);
                Assert.AreEqual(observed.StructuredContent.Value.GetRawText(), recovered.StructuredContent!.Value.GetRawText());
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-mcp-geometry-"
                    + request.Document.SheetPath.Path[^1].Value + ".json"), observed.StructuredContent.Value.GetRawText(), token);
            }
        }
        finally { Directory.Delete(state, true); }
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
