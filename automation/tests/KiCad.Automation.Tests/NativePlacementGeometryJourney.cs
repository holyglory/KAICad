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
