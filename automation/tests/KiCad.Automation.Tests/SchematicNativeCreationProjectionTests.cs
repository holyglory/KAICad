using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNativeCreationProjectionTests
{
    [TestMethod]
    public void AddsAnUnconnectedKnownPartWithExactGeneratedNativeAndPinIdentities()
    {
        var (baseline, library) = Fixture();
        var originalXml = SchematicDesignXml.Write(baseline, [library]);
        var wanted = AddComponent(baseline);

        var result = SchematicNativeCreationProjection.Project(baseline, wanted, [library]);
        Assert.IsNotNull(result.Candidate);
        var newOccurrences = wanted.Circuit.Symbols.Where(s => baseline.Engineering.Circuit.Symbols.All(old => old.Id != s.Id)).ToArray();
        Assert.AreEqual(newOccurrences.Length, result.CreatedOccurrences.Count);
        Assert.AreEqual(newOccurrences.Select(s => result.Candidate.SymbolBindings.Single(b => b.SymbolOccurrenceId == s.Id).NativeObjectId).Distinct().Count(),
            result.Operations.Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true));
        var occurrenceId = newOccurrences[0].Id;
        var binding = result.Candidate.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrenceId);
        Assert.AreNotEqual(Guid.Empty, binding.NativeObjectId);
        Assert.IsTrue(result.CreatedOccurrences.Contains(occurrenceId));
        Assert.IsTrue(SchematicDesignBindings.Inspect(result.Candidate, [library]).IdentitiesResolved);
        Assert.AreEqual(originalXml, SchematicDesignXml.Write(baseline, [library]), "Creation must not mutate its baseline.");

        var created = result.Candidate.Schematic.Instances.SelectMany(s => s.Items)
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>())
            .Where(s => s.Id.Value == binding.NativeObjectId.ToString("D")).ToArray();
        Assert.IsNotEmpty(created);
        Assert.IsTrue(created.All(s => s.ReferenceField.Text.Text_ == "U?"));
        Assert.IsTrue(created.All(s => s.ValueField.Text.Text_ == "Created probe"));
        Assert.IsTrue(created.SelectMany(s => s.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            .Select(c => c.Item.Unpack<SchematicPin>()).All(p => p.Id is not null && p.LibraryPinId is not null));
        foreach (var symbol in created)
        {
            var paths = symbol.InstanceRecords.Records.Select(r => string.Join('/', r.Path.Select(p => p.Value))).ToArray();
            CollectionAssert.AreEqual(symbol.InstanceRecords.Records.OrderBy(r => r.Path.Count)
                .ThenBy(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal)
                .Select(r => string.Join('/', r.Path.Select(p => p.Value))).ToArray(), paths,
                "Native PackSymbol uses path depth before UUID order, not model occurrence UUID.");
        }
    }

    [TestMethod]
    public void DeterministicRetryProducesTheSameNativeAndPinIdentities()
    {
        var (baseline, library) = Fixture();
        var wanted = AddComponent(baseline);
        var first = SchematicNativeCreationProjection.Project(baseline, wanted, [library]);
        var second = SchematicNativeCreationProjection.Project(baseline, wanted, [library]);
        Assert.AreEqual(SchematicDataXml.Write(first.Candidate.Schematic), SchematicDataXml.Write(second.Candidate.Schematic));
        CollectionAssert.AreEqual(first.Candidate.SymbolBindings.ToArray(), second.Candidate.SymbolBindings.ToArray());
        CollectionAssert.AreEqual(first.Operations.ToArray(), second.Operations.ToArray());
    }

    // Must-catch for the likely cause of the refusal in governed run t20260924T114705Z-b90471 (its recording was not kept): a
    // new probe copying a placed probe whose reference field the journey's manual field check had dragged 60.96 mm aside and
    // 121.666 mm in front of its pin (as it left one in governed run t20260924T154856Z-66ef97) would carry that field, and its
    // own measured bounds would cover all the room for its connection. Which placed symbol creation copies depends on
    // generated identities, so the live journey cannot pick it; here two editors hold the same part with the same library
    // definition, but every placed symbol's fields dragged, turned, hidden or shown, resized and restyled differently. Both
    // must create the identical symbol, with each field the library defines laid out exactly as the library lays it out
    // (position, every text attribute, visibility, name display, auto-placement and privacy, as KiCad places a symbol from
    // its library), and a field only the placed symbols have kept beside the symbol.
    [TestMethod]
    public void CreatedSymbolsTakeTheirFieldPlacesFromTheLibraryNotFromTheSymbolTheyCopy()
    {
        const long Mm = 1_000_000;
        var (baseline, library) = Fixture();
        var wanted = AddComponent(baseline);
        static SchematicField Field(string name, string text, long x, long y, int degrees, HorizontalAlignment alignment, bool visible = true,
            long size = 1_270_000, bool bold = false, bool italic = false, bool showName = false, bool autoplace = true) => new()
        {
            Name = name, Visible = visible, ShowName = showName, AllowAutoPlace = autoplace,
            Text = new() { Text_ = text, Position = new() { XNm = x, YNm = y }, Attributes = new() { Multiline = true, Angle = new() { ValueDegrees = degrees },
                HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.VaCenter, Size = new() { XNm = size, YNm = size },
                Bold = bold, Italic = italic } }
        };
        // The library definition every placed symbol of the part carries: its own fields, symbol-local, each styled its own way.
        var libraryReference = Field("Reference", "U", 0, -3 * Mm, 0, HorizontalAlignment.HaCenter);
        var libraryValue = Field("Value", "Probe", 0, -5 * Mm, 0, HorizontalAlignment.HaCenter, size: 1_524_000, bold: true, autoplace: false);
        // Private in the library, and public on every placed copy, so that the created field's privacy tells them apart.
        var libraryPart = Field("MPN", "P-1", 5 * Mm, 0, 0, HorizontalAlignment.HaLeft, visible: false, size: 1_000_000, italic: true, showName: true);
        libraryPart.IsPrivate = true;
        SchematicDesign Dragged(long aside, long ahead, int degrees, bool shown, long size)
        {
            var design = baseline with { Schematic = baseline.Schematic.Clone() };
            foreach (var screen in design.Schematic.Instances)
            for (int i = 0; i < screen.Items.Count; i++)
            {
                if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                symbol.Definition.ReferenceField = libraryReference.Clone();
                symbol.Definition.ValueField = libraryValue.Clone();
                symbol.Definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(libraryPart) });
                // Where a person dragged and turned this editor's fields, how they hid, showed, resized and restyled them, and a
                // field only the placed symbols have.
                long x = symbol.Position.XNm, y = symbol.Position.YNm;
                symbol.ReferenceField = Field("Reference", symbol.ReferenceField.Text.Text_, x + aside, y + ahead, degrees, HorizontalAlignment.HaLeft,
                    visible: shown, size: size, italic: shown, showName: shown, autoplace: !shown);
                symbol.ValueField = Field("Value", symbol.ValueField.Text.Text_, x - aside, y + ahead, degrees, HorizontalAlignment.HaRight,
                    visible: !shown, size: size, bold: !shown, autoplace: shown);
                symbol.UserFields.Add(Field("MPN", "P-1", x + aside, y - ahead, degrees, HorizontalAlignment.HaRight, visible: shown, size: size,
                    showName: !shown));
                symbol.UserFields.Add(Field("Note", "placed only", x + 7 * Mm, y + 7 * Mm, 0, HorizontalAlignment.HaLeft));
                symbol.FieldsAutoplaced = true;
                screen.Items[i] = Any.Pack(symbol);
            }
            return design;
        }
        SchematicSymbolInstance[] Created(SchematicDesign design)
        {
            var result = SchematicNativeCreationProjection.Project(design, wanted, [library]);
            var ids = result.CreatedOccurrences.Select(o => result.Candidate.SymbolBindings.Single(b => b.SymbolOccurrenceId == o).NativeObjectId.ToString("D")).ToHashSet();
            return [.. result.Candidate.Schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => ids.Contains(s.Id.Value)).OrderBy(s => s.Id.Value, StringComparer.Ordinal)
                .ThenBy(s => string.Join('/', s.Path.Path.Select(p => p.Value)), StringComparer.Ordinal)];
        }
        var near = Created(Dragged(60_960_000, 121_666_000, 90, shown: false, size: 2_540_000));
        var far = Created(Dragged(-20 * Mm, 40 * Mm, 270, shown: true, size: 800_000));
        // The two refusals governed full acceptance recorded on sources without this rule (runs t20260925T024611Z-78215b and
        // t20260925T090725Z-c388f6, "no free room for a connection stub" at the new probe's pin). The manual field check drags
        // one placed probe's reference to the sheet point (81.28 mm, 141.986 mm), and in both runs that probe had the lowest
        // generated occurrence identity of its part, so creation copied it: TP801 at (20.32 mm, 20.32 mm), its reference
        // 60.96 mm aside and 121.666 mm ahead (the drag above), and TP899 at (200.66 mm, 130.81 mm), its reference 119.38 mm
        // to the other side and 11.176 mm ahead (the drag here).
        var refusedInAcceptance = Created(Dragged(-119_380_000, 11_176_000, 0, shown: true, size: 1_270_000));
        Assert.IsNotEmpty(near);
        CollectionAssert.AreEqual(near, far,
            "The new symbol does not depend on where the copied symbol's fields were dragged or how they were shown, sized or styled.");
        CollectionAssert.AreEqual(near, refusedInAcceptance, "A copy of the probe dragged as in run t20260925T090725Z-c388f6 is the same new symbol.");
        foreach (var symbol in near)
        {
            long x = symbol.Position.XNm, y = symbol.Position.YNm;
            Assert.AreEqual(new Kiapi.Common.Types.Vector2 { XNm = x, YNm = y - 3 * Mm }, symbol.ReferenceField.Text.Position, "The reference sits where the library puts it.");
            Assert.AreEqual(new Kiapi.Common.Types.Vector2 { XNm = x, YNm = y - 5 * Mm }, symbol.ValueField.Text.Position, "The value sits where the library puts it.");
            var mpn = symbol.UserFields.Single(f => f.Name == "MPN");
            Assert.AreEqual(new Kiapi.Common.Types.Vector2 { XNm = x + 5 * Mm, YNm = y }, mpn.Text.Position, "A user field the library defines sits where the library puts it.");
            Assert.IsTrue(mpn.IsPrivate, "The field is private as the library field is, though every placed copy of it is public.");
            foreach (var (field, source) in new[] { (symbol.ReferenceField, libraryReference), (symbol.ValueField, libraryValue), (mpn, libraryPart) })
            {
                Assert.AreEqual(source.Text.Attributes, field.Text.Attributes, field.Name + " takes every text attribute of the library field.");
                Assert.AreEqual(source.Visible, field.Visible, field.Name + " is shown or hidden as the library field is.");
                Assert.AreEqual(source.ShowName, field.ShowName, field.Name + " shows its name as the library field does.");
                Assert.AreEqual(source.AllowAutoPlace, field.AllowAutoPlace, field.Name + " may be placed automatically as the library field may.");
                Assert.AreEqual(source.IsPrivate, field.IsPrivate, field.Name + " is private as the library field is.");
            }
            Assert.AreNotEqual("U", symbol.ReferenceField.Text.Text_, "The text is the component's own, not the library's.");
            Assert.AreNotEqual("Probe", symbol.ValueField.Text.Text_, "The value is the component's own, not the library's.");
            var note = symbol.UserFields.Single(f => f.Name == "Note");
            Assert.AreEqual(new Kiapi.Common.Types.Vector2 { XNm = x + 7 * Mm, YNm = y + 7 * Mm }, note.Text.Position,
                "A field the library does not define keeps its place beside the symbol.");
            Assert.IsTrue(note.Visible); Assert.AreEqual(1_270_000, note.Text.Attributes.Size.XNm, "and keeps its own style.");
            Assert.IsFalse(symbol.FieldsAutoplaced, "Nothing arranged the fields automatically.");
        }
    }

    [TestMethod]
    public void ConnectedOrCoordinateFreeCreationIsRejectedWithoutAPartialCandidate()
    {
        var (baseline, library) = Fixture();
        var wanted = AddComponent(baseline);
        var baselineIds = baseline.Engineering.Circuit.Components.Select(c => c.Id).ToHashSet();
        var component = wanted.Circuit.Components.First(c => !baselineIds.Contains(c.Id));
        var definition = wanted.Circuit.Sheets.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
        var part = wanted.Circuit.Parts.Single(p => p.Id == definition.PartId);
        var connected = wanted with
        {
            Circuit = wanted.Circuit with
            {
                Nets = [.. wanted.Circuit.Nets, new(Guid.NewGuid(), "new connection", [new(component.Id, part.Pins[0].Number)])]
            }
        };
        Assert.AreEqual("created_component_connectivity_requires_resolution",
            Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(baseline, connected, [library])).Code);

        var coordinateFree = AddComponent(baseline, coordinateFree: true);
        Assert.AreEqual("created_symbol_placement_required",
            Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(baseline, coordinateFree, [library])).Code);

        // An admitted connected addition (cn1-wiring-intent.md §4.3) lifts exactly the connectivity refusal:
        // the connected request places the same symbols with the same identities and operations as the
        // unconnected one, keeps its nets for the connection plan, and every other refusal still applies.
        var unconnected = SchematicNativeCreationProjection.Project(baseline, wanted, [library]);
        var allowed = SchematicNativeCreationProjection.Project(baseline, connected, [library], allowConnected: true);
        Assert.AreEqual(SchematicDataXml.Write(unconnected.Candidate.Schematic), SchematicDataXml.Write(allowed.Candidate.Schematic));
        CollectionAssert.AreEqual(unconnected.Candidate.SymbolBindings.ToArray(), allowed.Candidate.SymbolBindings.ToArray());
        CollectionAssert.AreEqual(unconnected.Operations.ToArray(), allowed.Operations.ToArray());
        Assert.AreEqual(connected.Circuit.Nets.Last(), allowed.Candidate.Engineering.Circuit.Nets.Last());
        Assert.IsTrue(SchematicDesignBindings.Inspect(allowed.Candidate, [library]).IdentitiesResolved);
        var coordinateFreeConnected = coordinateFree with { Circuit = coordinateFree.Circuit with { Nets = [.. coordinateFree.Circuit.Nets,
            new(Guid.NewGuid(), "new connection", [new(coordinateFree.Circuit.Components.First(c => !baselineIds.Contains(c.Id)).Id, part.Pins[0].Number)])] } };
        Assert.AreEqual("created_component_connectivity_requires_resolution", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicNativeCreationProjection.Project(baseline, coordinateFreeConnected, [library])).Code, "the connectivity refusal comes first");
        Assert.AreEqual("created_symbol_placement_required", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicNativeCreationProjection.Project(baseline, coordinateFreeConnected, [library], allowConnected: true)).Code);

        // The frozen PSU-CPU circuit with all eleven nets: refused as before, and with connections admitted
        // it creates exactly the eleven placements of the unconnected Components stage.
        var (sheets, components) = PsuCpuComponents();
        var complete = components with { Engineering = components.Engineering with { Circuit = components.Engineering.Circuit with
            { Nets = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets } } };
        Assert.AreEqual("created_component_connectivity_requires_resolution",
            Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(sheets, complete, [])).Code);
        var fixturePlaced = SchematicNativeCreationProjection.Project(sheets, components, []);
        var fixtureConnected = SchematicNativeCreationProjection.Project(sheets, complete, [], allowConnected: true);
        Assert.AreEqual(SchematicDataXml.Write(fixturePlaced.Candidate.Schematic), SchematicDataXml.Write(fixtureConnected.Candidate.Schematic));
        CollectionAssert.AreEqual(fixturePlaced.Operations.ToArray(), fixtureConnected.Operations.ToArray());
        Assert.AreEqual(11, fixtureConnected.CreatedOccurrences.Count);
        Assert.AreEqual(41, fixtureConnected.Candidate.Engineering.Circuit.Nets.Sum(n => n.Pins.Count));
    }

    // Supporting evidence for the frozen PSU-CPU target case (contract §1.4.2, §1.6.3): processor U5
    // declares four units and places unit 4 on CPU_POWER, a child of its own CPU sheet. The rendered
    // NativeXmlComponentCreation journey proves cross-sheet declared creation end to end on its probe
    // fixture; this checks the exact fixture circuit, part pins and expected placements offline.
    [TestMethod]
    public void FixtureProcessorUnitFourIsCreatedOnCpuPowerAsTheSameComponent()
    {
        var (baseline, desired) = PsuCpuComponents();
        string before = SchematicDesignXml.Write(baseline, []);
        var result = SchematicNativeCreationProjection.Project(baseline, desired, []);
        Assert.AreEqual(before, SchematicDesignXml.Write(baseline, []), "Creation must not mutate its baseline.");
        Assert.IsTrue(SchematicDesignBindings.Inspect(result.Candidate, []).IdentitiesResolved);
        var circuit = result.Candidate.Engineering.Circuit;
        CollectionAssert.AreEquivalent(circuit.Symbols.Select(s => s.Id).ToArray(), result.CreatedOccurrences.ToArray());

        // Every expected placement of the Components stage, including U5 unit 4 on CPU_POWER.
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Components);
        var sheetPaths = expected.Sheets.ToDictionary(s => s.Key, s => SchematicDesignBindings.PathKey(
            result.Candidate.SheetBindings.Single(b => b.SheetInstanceId == s.ModelSheetInstance).NativePath));
        var bindings = result.Candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var screens = result.Candidate.Schematic.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)));
        var created = new Dictionary<Guid, SchematicSymbolInstance>();
        foreach (var symbol in expected.Symbols)
        {
            var native = screens[sheetPaths[symbol.Sheet]].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == bindings[symbol.Occurrence]);
            // Saved and reloaded form: the cache key equals the library identifier, so no separate alias is kept, and
            // pin-name spacing stays on the definition, never on the placed symbol.
            Assert.AreEqual((symbol.Reference, symbol.Value, symbol.LibId, symbol.Unit, "", 0L),
                (native.ReferenceField.Text.Text_, native.ValueField.Text.Text_, native.LibraryId.LibraryNickname + ":" + native.LibraryId.EntryName,
                    native.Unit.Unit, native.LibName, native.PinNameOffset.ValueNm), symbol.Sheet);
            Assert.AreEqual(508000L, native.DefinitionPinNameOffset.ValueNm, symbol.Sheet);
            var record = native.InstanceRecords.Records.Single();
            Assert.AreEqual((sheetPaths[symbol.Sheet], symbol.Reference, symbol.Unit),
                (string.Join('/', record.Path.Select(p => p.Value)), record.Reference, record.Unit));
            created.Add(symbol.Occurrence, native);
        }
        Assert.AreEqual(expected.Symbols.Count, screens.Values.Sum(s => s.Items.Count(i => i.Is(SchematicSymbolInstance.Descriptor))));
        // Each sheet's library cache is in KiCad's own key order (the PSU sheet receives six definitions out of that order),
        // so the published XML lists it as KiCad reports, saves and reloads it.
        foreach (var sheet in expected.Sheets.Where(s => expected.Symbols.Any(x => x.Sheet == s.Key)))
        {
            var keys = screens[sheetPaths[sheet.Key]].CachedSymbols.Select(c => c.CacheKey).ToArray();
            CollectionAssert.AreEqual(keys.Order(StringComparer.Ordinal).ToArray(), keys, sheet.Key);
        }
        CollectionAssert.AreEqual(new[] { "Battery_Management:LTC2959", "Connector_Generic:Conn_01x02", "Device:R", "MCU_ST_STM32C0:STM32C011J_4-6_Mx",
            "Regulator_Linear:LP3982ILD-3.3", "Regulator_Switching:LM2595S-ADJ" }, screens[sheetPaths["PSU"]].CachedSymbols.Select(c => c.CacheKey).ToArray());

        // U5: one component, four native symbols on two sheets, one declared definition and exact unit pins.
        var u5 = circuit.Components.Single(c => c.Reference == "U5");
        var part = circuit.Parts.Single(p => p.Id == circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == u5.DefinitionId).PartId);
        var units = circuit.Symbols.Where(s => s.ComponentId == u5.Id).OrderBy(s => s.Unit).ToArray();
        CollectionAssert.AreEqual(new[] { "CPU", "CPU", "CPU", "CPU_POWER" },
            units.Select(s => expected.Sheets.Single(x => x.ModelSheetInstance == s.EffectiveSheetInstanceId(u5)).Key).ToArray());
        Assert.AreEqual<Guid?>(PsuCpuIds.Id(0x05, 4), units[3].SheetInstanceId);
        Assert.AreEqual(4, units.Select(s => bindings[s.Id]).Distinct().Count());
        Assert.IsFalse(part.Pins.Any(p => p.Unit == 0), "The fixture processor has no pins common to all units.");
        var declaration = desired.PartSymbols!.Single(s => s.PartId == part.Id);
        var libraryPins = declaration.Symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor) && (c.BodyStyle?.Style ?? 0) is 0 or 1)
            .Select(c => c.Item.Unpack<SchematicPin>().Id.Value).Order(StringComparer.Ordinal).ToArray();
        var allPlaced = new List<string>();
        foreach (var unit in units)
        {
            var symbol = created[unit.Id];
            Assert.AreEqual(declaration.LibraryId, symbol.LibraryId);
            Assert.AreEqual(declaration.Symbol.Definition.Id, symbol.Definition.Id);
            var active = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>())).Where(p => p.Pin.LibraryPinId is not null).ToArray();
            CollectionAssert.AreEqual(libraryPins, active.Select(p => p.Pin.LibraryPinId.Value).Order(StringComparer.Ordinal).ToArray(),
                "Every unit carries the one declared definition with its owned pin identities.");
            var placed = active.Where(p => p.Child.Unit.Unit == 0 || p.Child.Unit.Unit == unit.Unit)
                .Select(p => p.Pin.Number).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(part.Pins.Where(p => p.Unit == unit.Unit).Select(p => p.Number).Order(StringComparer.Ordinal).ToArray(), placed);
            allPlaced.AddRange(placed);
        }
        CollectionAssert.AreEquivalent(part.Pins.Select(p => p.Number).ToArray(), allPlaced.ToArray(),
            "The four units cover all 177 processor pins exactly once across both sheets.");
        foreach (string sheet in new[] { "CPU", "CPU_POWER" })
            Assert.IsTrue(SchematicLibraryCacheEquivalence.Equal(declaration.Symbol,
                screens[sheetPaths[sheet]].CachedSymbols.Single(c => c.CacheKey == declaration.Symbol.CacheKey)), sheet);
        CollectionAssert.AreEquivalent(new[] { (sheetPaths["PSU"], 6), (sheetPaths["CPU"], 4), (sheetPaths["CPU_POWER"], 1) },
            result.Operations.Where(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true)
                .GroupBy(o => string.Join('/', o.TargetDocument.SheetPath.Path.Select(p => p.Value))).Select(g => (g.Key, g.Count())).ToArray());

        var retry = SchematicNativeCreationProjection.Project(baseline, desired, []);
        Assert.AreEqual(SchematicDesignXml.Write(result.Candidate, []), SchematicDesignXml.Write(retry.Candidate, []));
        CollectionAssert.AreEqual(result.Operations.ToArray(), retry.Operations.ToArray());
    }

    [TestMethod]
    public void InconsistentCrossSheetUnitDeclarationsAreRejectedWithoutAPartialCandidate()
    {
        var (baseline, desired) = PsuCpuComponents();
        string before = SchematicDesignXml.Write(baseline, []);
        var u5 = desired.Engineering.Circuit.Components.Single(c => c.Reference == "U5");
        SchematicDesign Units(Func<SymbolOccurrence, SymbolOccurrence?> edit) => desired with { Engineering = desired.Engineering with
        { Circuit = desired.Engineering.Circuit with { Symbols = desired.Engineering.Circuit.Symbols
            .Select(s => s.ComponentId == u5.Id ? edit(s) : s).OfType<SymbolOccurrence>().ToArray() } } };
        void Rejected(string code, SchematicDesign wanted, SchematicDesign? from = null, string problem = "")
        {
            string declared = SchematicDesignXml.Write(desired, []);
            Assert.AreEqual(code, Assert.ThrowsExactly<AutomationException>(() =>
                SchematicNativeCreationProjection.Project(from ?? baseline, wanted, []), problem).Code, problem);
            Assert.AreEqual(before, SchematicDesignXml.Write(baseline, []), problem);
            Assert.AreEqual(declared, SchematicDesignXml.Write(desired, []), problem);
        }
        Rejected("invalid_circuit", Units(s => s.Unit == 4 ? s with { SheetInstanceId = Guid.NewGuid() } : s), problem: "unknown sheet");
        Rejected("component_units_incomplete", Units(s => s.Unit == 4 ? null : s), problem: "missing unit 4");

        // The CPU_POWER target already caches a different definition under the processor's key.
        var conflicting = baseline with { Schematic = baseline.Schematic.Clone() };
        var cpuPower = SchematicDesignBindings.PathKey(baseline.SheetBindings.Single(b => b.SheetInstanceId == PsuCpuIds.Id(0x05, 4)).NativePath);
        var processor = desired.PartSymbols!.Single(s => s.Symbol.CacheKey == "Library:F28P659DK8PTPQ1").Symbol.Clone();
        processor.ShowPinNumbers = !processor.ShowPinNumbers;
        conflicting.Schematic.Instances.Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == cpuPower)
            .CachedSymbols.Add(processor);
        Rejected("created_symbol_cache_conflict", desired, conflicting, "cache conflict on the unit 4 sheet");

        // A declaration that lacks one unit-4 pin cannot cover the part exactly.
        var incomplete = desired.PartSymbols!.Select(s => s with { Symbol = s.Symbol.Clone() }).ToArray();
        var definition = incomplete.Single(s => s.Symbol.CacheKey == "Library:F28P659DK8PTPQ1").Symbol.Definition;
        definition.Items.Remove(definition.Items.First(c => c.Item.Is(SchematicPin.Descriptor) && c.Unit.Unit == 4));
        Rejected("invalid_part_symbol", desired with { PartSymbols = incomplete }, problem: "unit 4 pin missing");
    }

    [TestMethod]
    public void UnitsFromRepeatedSheetsOnASingleInstanceSheetStaySeparateSymbols()
    {
        // Both channel components (one repeated sheet definition) move unit 2 to the root sheet.
        var moved = Repeated((owner, unit, root) => unit == 2 && owner != root ? root : null);
        var result = SchematicNativeCreationProjection.Project(moved.Baseline, moved.Desired, [moved.Library]);
        Assert.IsTrue(SchematicDesignBindings.Inspect(result.Candidate, [moved.Library]).IdentitiesResolved);
        var bindings = result.Candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var paths = result.Candidate.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var symbols = result.Candidate.Schematic.Instances.SelectMany(s => s.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => (Path: string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)), Symbol: i.Unpack<SchematicSymbolInstance>())))
            .ToDictionary(p => p.Path + "#" + p.Symbol.Id.Value, p => p.Symbol);
        var occurrences = moved.Desired.Engineering.Circuit.Symbols.Where(s => moved.Components.Any(c => c.Id == s.ComponentId)).ToArray();
        var units2 = occurrences.Where(s => s.Unit == 2 && s.SheetInstanceId == moved.Root).ToArray();
        Assert.HasCount(2, units2);
        Assert.AreEqual(2, units2.Select(s => bindings[s.Id]).Distinct().Count());
        foreach (var unit in units2)
        {
            var owner = moved.Components.Single(c => c.Id == unit.ComponentId);
            var symbol = symbols[paths[moved.Root] + "#" + bindings[unit.Id]];
            var record = symbol.InstanceRecords.Records.Single();
            Assert.AreEqual((paths[moved.Root], owner.Reference, 2), (string.Join('/', record.Path.Select(p => p.Value)), record.Reference, record.Unit));
            Assert.AreEqual(owner.Reference, symbol.ReferenceField.Text.Text_);
        }
        var shared = occurrences.Where(s => s.Unit == 1 && moved.Components.Single(c => c.Id == s.ComponentId).SheetInstanceId != moved.Root).ToArray();
        Assert.AreEqual(1, shared.Select(s => bindings[s.Id]).Distinct().Count(), "The channel unit 1 stays one physical symbol.");
        var sharedSymbol = symbols[paths[shared[0].EffectiveSheetInstanceId(moved.Components.Single(c => c.Id == shared[0].ComponentId))] + "#" + bindings[shared[0].Id]];
        CollectionAssert.AreEquivalent(shared.Select(s => (paths[moved.Components.Single(c => c.Id == s.ComponentId).SheetInstanceId],
                moved.Components.Single(c => c.Id == s.ComponentId).Reference)).ToArray(),
            sharedSymbol.InstanceRecords.Records.Select(r => (string.Join('/', r.Path.Select(p => p.Value)), r.Reference)).ToArray());
        Assert.AreEqual(4, result.Operations.Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true
            && SchematicDesignBindings.PathKey(o.TargetDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray()) == paths[moved.Root]));

        // Shared identities do not depend on another group being split.
        var own = Repeated((_, _, _) => null, moved);
        var ownResult = SchematicNativeCreationProjection.Project(own.Baseline, own.Desired, [own.Library]);
        var ownBindings = ownResult.Candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
        foreach (var occurrence in occurrences.Where(s => s.SheetInstanceId is null))
            Assert.AreEqual(bindings[occurrence.Id], ownBindings[occurrence.Id].ToString("D"));
        var retry = SchematicNativeCreationProjection.Project(moved.Baseline, moved.Desired, [moved.Library]);
        Assert.AreEqual(SchematicDesignXml.Write(result.Candidate, [moved.Library]), SchematicDesignXml.Write(retry.Candidate, [moved.Library]));

        // Identity precondition: whether a unit gets the shared or a per-component identity depends on every
        // occurrence of its definition, so a caller that supplies only some of them is refused as a defect
        // before any identity is computed. Complete sets are accepted per definition and give the same groups.
        var circuit = moved.Desired.Engineering.Circuit;
        var channel = moved.Components.Where(c => c.SheetInstanceId != moved.Root).OrderBy(c => c.Id).ToArray();
        foreach (var (problem, partial) in new (string, SymbolOccurrence[])[]
        {
            ("one channel component of the shared definition", [.. occurrences.Where(s => s.ComponentId != channel[0].Id)]),
            ("only the moved units", units2),
            ("one moved unit", [units2[0]]),
            ("a channel component without its moved unit", [.. occurrences.Where(s => s.Id != units2[0].Id)])
        })
            Assert.ThrowsExactly<InvalidOperationException>(() => SchematicNativeCreationProjection.PhysicalSymbols(moved.Baseline, circuit, partial), problem);
        var complete = SchematicNativeCreationProjection.PhysicalSymbols(moved.Baseline, circuit, occurrences).Select(g => g.Key).ToArray();
        var rootDefinition = moved.Components.Single(c => c.SheetInstanceId == moved.Root).DefinitionId;
        var perDefinition = new[] { rootDefinition, channel[0].DefinitionId }.SelectMany(definition => SchematicNativeCreationProjection.PhysicalSymbols(
            moved.Baseline, circuit, occurrences.Where(s => moved.Components.Single(c => c.Id == s.ComponentId).DefinitionId == definition)))
            .Select(g => g.Key).ToArray();
        CollectionAssert.AreEquivalent(complete, perDefinition, "Each definition's identities depend only on its own complete occurrence set.");
    }

    [TestMethod]
    public void UnitsThatReachOnlySomeRepeatedSheetInstancesAreRejected()
    {
        // The root component's unit 2 on one repeated channel only, and both channel components' unit 2
        // on the same channel: either way one channel instance would show a unit no occurrence owns.
        foreach (var (problem, place) in new (string, Func<Guid, int, Guid, Guid?>)[]
        {
            ("root unit on one channel", (owner, unit, root) => unit == 2 && owner == root ? First : null),
            ("two channel units on one channel", (owner, unit, root) => unit == 2 && owner != root ? First : null)
        })
        {
            var inconsistent = Repeated(place);
            string before = SchematicDesignXml.Write(inconsistent.Baseline, [inconsistent.Library]);
            Assert.AreEqual("created_unit_sheet_coverage_mismatch", Assert.ThrowsExactly<AutomationException>(() =>
                SchematicNativeCreationProjection.Project(inconsistent.Baseline, inconsistent.Desired, [inconsistent.Library]), problem).Code, problem);
            Assert.AreEqual(before, SchematicDesignXml.Write(inconsistent.Baseline, [inconsistent.Library]), problem);
        }
    }

    // Ledger pb41c5714361c378a, CN-1 §5.3 on the unconnected creation path: a created pin that KiCad joins to a global net by
    // name alone may stay out of every XML net only while nothing else in the design has that name. A unit test on purpose:
    // the rendered NativeXmlComponentCreation journey proves the refused and the declared case end to end on a live KiCad
    // with one hidden power pin name, and the intent builder's power-net test (the nearest existing test, which covers the
    // connected path) is extended with the same states on this path. The other sources KiCad joins by name (global labels,
    // power symbols, pins of the same creation), the unresolved power name and the cases that must stay admitted (stacked
    // pins, local labels, visible power inputs, a name used nowhere else) need states no single live fixture holds.
    [TestMethod]
    public void UnconnectedCreationIsRefusedWhenKiCadWouldJoinACreatedPinByNameAlone()
    {
        // The frozen PSU/CPU Components stage created on its empty S1 sheets, with chosen library pins made hidden power inputs.
        var (sheets, components) = PsuCpuComponents();
        SchematicDesign Hidden(params (string CacheKey, string Number)[] pins) => components with { PartSymbols =
        [
            .. SchematicSynchronizationPlanTests.WithLibraryPinTypes(components.PartSymbols!).Select(declaration =>
            {
                var symbol = declaration.Symbol.Clone();
                foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
                {
                    var pin = child.Item.Unpack<SchematicPin>();
                    if (!pins.Contains((symbol.CacheKey, pin.Number))) continue;
                    pin.ElectricalType = ElectricalPinType.EptPowerInput; pin.Visible = false;
                    child.Item = Any.Pack(pin);
                }
                return declaration with { Symbol = symbol };
            })
        ] };
        AutomationException Refused(SchematicDesign baseline, SchematicDesign desired, string code, string problem)
        {
            string before = SchematicDesignXml.Write(baseline, []);
            var error = Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(baseline, desired, []), problem);
            Assert.AreEqual(code, error.Code, problem + ": " + error.Message);
            StringAssert.Contains(error.Message, "nothing was changed", problem);
            Assert.AreEqual(before, SchematicDesignXml.Write(baseline, []), problem + ": creation must not change its baseline.");
            // CN-1 §4.3: a connected addition skips only the refusal of new nets. These revisions declare no net for the
            // created pins, so the connected mode refuses them the same way.
            var connected = Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(baseline, desired, [], allowConnected: true), problem);
            Assert.AreEqual((error.Code, error.Message), (connected.Code, connected.Message), problem + ": both modes apply the same rule.");
            return error;
        }

        // Guard: the memory's hidden VCC pin is the only VCC in the design, so KiCad joins it to nothing.
        var alone = SchematicNativeCreationProjection.Project(sheets, Hidden(("pic_programmer:24C16", "8")), []);
        Assert.IsTrue(CreatedPins(alone.Candidate, "U6").Any(p => p.Number == "8" && !p.Visible && p.ElectricalType == ElectricalPinType.EptPowerInput));
        // Must-catch: the ADC's and the memory's hidden GND pins would join each other, which the XML does not declare.
        var twoGround = Refused(sheets, Hidden(("Battery_Management:LTC2959", "10"), ("pic_programmer:24C16", "4")),
            SchematicConnectionErrors.ConnectedImplicitPowerConflict, "two new hidden GND pins");
        StringAssert.Contains(twoGround.Message, "Hidden power pin U3.10 is named 'GND'");
        StringAssert.Contains(twoGround.Message, "including hidden power pin U6.4");
        StringAssert.Contains(twoGround.Message, "Declare a net holding U3.10 and U6.4 so the connection is declared");
        // Guard: the regulator's two OUT pins stacked at one point are one connection in KiCad, so they may share the name.
        _ = SchematicNativeCreationProjection.Project(sheets, Hidden(("Regulator_Linear:LP3982ILD-3.3", "1"), ("Regulator_Linear:LP3982ILD-3.3", "4")), []);
        // Must-catch: a global label VCC already on the root sheet; guard: a local label VCC joins only its own sheet.
        SchematicDesign WithLabel(IMessage label)
        {
            var schematic = sheets.Schematic.Clone();
            schematic.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1).Items.Add(Any.Pack(label));
            return sheets with { Schematic = schematic };
        }
        var global = Refused(WithLabel(new GlobalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new(), Text = new() { Text_ = "VCC" },
                SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked }),
            Hidden(("pic_programmer:24C16", "8")), SchematicConnectionErrors.ConnectedImplicitPowerConflict, "global label VCC");
        StringAssert.Contains(global.Message, "including the global label 'VCC'");
        StringAssert.Contains(global.Message, "Add U6.8 to the XML net whose pins the global label 'VCC' connects so the connection is declared, or rename that label");
        _ = SchematicNativeCreationProjection.Project(WithLabel(new LocalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new(),
            Text = new() { Text_ = "VCC" }, SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = LockedState.LsUnlocked }), Hidden(("pic_programmer:24C16", "8")), []);

        // New global power symbols copied from #PWR1 (VCC) beside a global label V5, a local label V3 and an IC whose VCC
        // power input is visible, which KiCad joins by wire only.
        var bench = new SchematicConnectionIntentBuilderTests.Bench();
        Guid power = bench.Part("PWR", SchematicSymbolType.SstGlobalPower,
            new SchematicConnectionIntentBuilderTests.BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        Guid ic = bench.Part("IC", new SchematicConnectionIntentBuilderTests.BenchPin("1", "VCC", 1, ElectricalPinType.EptPowerInput),
            new SchematicConnectionIntentBuilderTests.BenchPin("2", "OUT", 1, ElectricalPinType.EptOutput));
        bench.Component(power, "#PWR1", value: "VCC"); bench.Component(ic, "U1");
        bench.GlobalLabel(SchematicConnectionIntentBuilderTests.BenchSheet.Root, "V5");
        bench.LocalLabel(SchematicConnectionIntentBuilderTests.BenchSheet.Root, "V3");
        var state = bench.State([]);
        SchematicDesign NewPower(string value, SchematicDesign? onto = null, string reference = "#PWR2") =>
            bench.Create(onto ?? state.Baseline, power, reference, SchematicConnectionIntentBuilderTests.BenchSheet.Root, value).Design;
        StringAssert.Contains(Refused(state.Baseline, NewPower("VCC"), SchematicConnectionErrors.ConnectedGlobalNameConflict, "power symbol VCC").Message,
            "Power symbol #PWR2 is named 'VCC', and KiCad joins everything with that name into one net, including power symbol #PWR1");
        StringAssert.Contains(Refused(state.Baseline, NewPower("V5"), SchematicConnectionErrors.ConnectedGlobalNameConflict, "power symbol V5").Message,
            "including the global label 'V5'");
        StringAssert.Contains(Refused(state.Baseline, NewPower("V7", NewPower("V7"), "#PWR3"), SchematicConnectionErrors.ConnectedGlobalNameConflict,
            "two new power symbols V7").Message, "including power symbol #PWR3");
        Assert.AreEqual(SchematicConnectionErrors.ConnectedPowerNameUnresolved, Assert.ThrowsExactly<AutomationException>(() =>
            SchematicNativeCreationProjection.Project(state.Baseline, NewPower("${RAIL}"), [])).Code);
        _ = SchematicNativeCreationProjection.Project(state.Baseline, NewPower("V3"), []);
        _ = SchematicNativeCreationProjection.Project(state.Baseline, bench.Create(state.Baseline, ic, "U2", SchematicConnectionIntentBuilderTests.BenchSheet.Root).Design, []);

        // Through the synchronization planner the refusal is a plan without a candidate or any native operation, with or
        // without a handshake that advertises connection realization.
        var (saved, _) = SchematicConnectionIntentBuilderTests.Revise(state, _ => NewPower("VCC"));
        Assert.IsTrue(SchematicNativeCreationProjection.IsSupportedAddition(saved.Baseline, DesignRecoveryStore.ReadDesired(saved).Engineering));
        foreach (var plan in new[] { SchematicSynchronizationPlanner.Plan(saved), SchematicConnectionIntentBuilderTests.Plan(saved) })
        {
            Assert.AreEqual(SchematicConnectionErrors.ConnectedGlobalNameConflict, plan.ErrorCode, plan.ErrorMessage);
            Assert.IsNull(plan.Candidate); Assert.IsNull(plan.CandidateXml); Assert.IsEmpty(plan.NativeOperations); Assert.IsNull(plan.Connections);
        }
        var (clean, _) = SchematicConnectionIntentBuilderTests.Revise(state, _ => NewPower("V3"));
        var admitted = SchematicSynchronizationPlanner.Plan(clean);
        Assert.IsTrue(admitted.CanPrepare, admitted.ErrorCode + ": " + admitted.ErrorMessage);
        Assert.IsNotNull(admitted.CandidateXml, "An unconnected creation keeps its publishable preview.");

        static IEnumerable<SchematicPin> CreatedPins(SchematicDesign design, string reference)
        {
            var component = design.Engineering.Circuit.Components.Single(c => c.Reference == reference);
            var natives = design.Engineering.Circuit.Symbols.Where(s => s.ComponentId == component.Id)
                .Select(s => design.SymbolBindings.Single(b => b.SymbolOccurrenceId == s.Id).NativeObjectId.ToString("D")).ToHashSet();
            return design.Schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => natives.Contains(s.Id.Value))
                .SelectMany(s => s.Definition.Items).Where(c => c.Item.Is(SchematicPin.Descriptor)).Select(c => c.Item.Unpack<SchematicPin>());
        }
    }

    // Review of ledger pb41c5714361c378a: a pin common to all units is one physical pin however many units show it, as the
    // electrical comparison treats it, so a new part whose common hidden power input has a name used nowhere else is created,
    // unconnected or connected, and the partition it asserts joins every unit's placement of that pin (KiCad joins them by
    // name), together with each unit's pins stacked on it. A second source of the name is still refused, naming each pin once.
    // A unit test on purpose: which pins count as one source is isolated planning logic, and the rendered creation journey
    // creates such a part in KiCad and compares KiCad's own pin partition with the assertion.
    [TestMethod]
    public void APinCommonToAllUnitsIsOneSourceThatKiCadJoinsAcrossUnits()
    {
        var bench = new SchematicConnectionIntentBuilderTests.Bench();
        Guid r = bench.Part("R", SchematicConnectionIntentBuilderTests.Passive("1"), SchematicConnectionIntentBuilderTests.Passive("2"));
        Guid r1 = bench.Component(r, "R1");
        var state = bench.State([]);
        static ConnectionPinKey[] Keys(SchematicDesign design, params (Guid Component, string Number)[] pins) => SchematicConnectionIntentBuilderTests.Keys(design, pins);
        foreach (bool stacked in new[] { false, true })
        {
            string when = stacked ? "every pin drawn at one point" : "pins apart";
            var part = CommonPowerPart(stacked);
            var (design, ids) = WithCommonPowerPart(state.Baseline, part, "U5");
            Guid u5 = ids[0];
            // Guard: VCC is U5.1 alone, however many units show it, in both modes.
            var candidate = SchematicNativeCreationProjection.Project(state.Baseline, design, []).Candidate;
            _ = SchematicNativeCreationProjection.Project(state.Baseline, design, [], allowConnected: true);
            var common = Keys(candidate, (u5, "1"));
            Assert.HasCount(2, common, when + ": both units show the common pin.");
            ConnectionPinKey[][] expected = stacked ? [[.. common, .. Keys(candidate, (u5, "2"), (u5, "3"))]]
                : [common, Keys(candidate, (u5, "2")), Keys(candidate, (u5, "3"))];
            RequireAsserted(SchematicNativeCreationProjection.CreationAssertion(state.Baseline, candidate), expected, when);
            var (saved, _) = SchematicConnectionIntentBuilderTests.Revise(state, _ => design);
            var plan = SchematicSynchronizationPlanner.Plan(saved);
            Assert.IsTrue(plan.CanPrepare && plan.CandidateXml is not null, when + ": " + plan.ErrorCode + ": " + plan.ErrorMessage);

            // Must-catch: a second part of the same kind is a second source of VCC; the message names each pin once.
            var (twice, _) = WithCommonPowerPart(state.Baseline, part, "U5", "U6");
            var error = Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(state.Baseline, twice, []), when);
            Assert.AreEqual(SchematicConnectionErrors.ConnectedImplicitPowerConflict, error.Code, error.Message);
            StringAssert.Contains(error.Message, "Hidden power pin U5.1 is named 'VCC'", when);
            StringAssert.Contains(error.Message, "including hidden power pin U6.1", when);
            StringAssert.Contains(error.Message, "Declare a net holding U5.1 and U6.1", when);
            // Must-catch: an existing global label VCC.
            var labelled = state.Baseline with { Schematic = state.Baseline.Schematic.Clone() };
            labelled.Schematic.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1).Items.Add(Any.Pack(new GlobalLabel
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new(), Text = new() { Text_ = "VCC" },
                SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked
            }));
            var (onLabel, _) = WithCommonPowerPart(labelled, part, "U5");
            StringAssert.Contains(Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(labelled, onLabel, []), when).Message,
                "including the global label 'VCC'", when);
        }

        // A connected addition of the same part: OUT joins U5.2 and R1.1, and the common hidden VCC pin, in no net, is one
        // expected group of both units' placements, as KiCad joins them.
        var apart = CommonPowerPart(stacked: false);
        var (connected, created) = WithCommonPowerPart(state.Baseline, apart, "U5");
        var (connectedSaved, _) = SchematicConnectionIntentBuilderTests.Revise(state, _ => SchematicConnectionIntentBuilderTests.WithNets(connected,
            new CircuitNet(Guid.NewGuid(), "OUT", [new(created[0], "2"), new(r1, "1")])));
        var realization = SchematicConnectionIntentBuilderTests.Plan(connectedSaved);
        var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(realization);
        SchematicConnectionIntentBuilderTests.RequireGroups(intent, [Keys(realization.Candidate!, (created[0], "1")),
            Keys(realization.Candidate!, (created[0], "2"), (r1, "1")), Keys(realization.Candidate!, (created[0], "3"))]);

        static void RequireAsserted(KiCad.Automation.Protocol.SchematicItemOperation operation, ConnectionPinKey[][] expected, string when)
        {
            var actual = operation.AssertConnectivity.ExpectedGroups.Select(g => g.Pins.Select(p =>
                new ConnectionPinKey(string.Join('/', p.Path.Path.Select(id => id.Value)), Guid.Parse(p.Pin.Value)))).ToArray();
            CollectionAssert.AreEquivalent(expected.Select(g => Joined(g)).ToArray(), actual.Select(g => Joined(g)).ToArray(), when);
        }

        static string Joined(IEnumerable<ConnectionPinKey> group) =>
            string.Join(",", group.Select(k => k.SheetPathKey + "/" + k.PlacedPinId.ToString("D")).Order(StringComparer.Ordinal));
    }

    // Ledger p2d40d4ec87d01d32: a new part whose one symbol draws two hidden power inputs of one name (VSTK) at one point. KiCad
    // makes them one connection, so planning treats them as one when the XML also adds connections, as the unconnected path
    // does: both left out of every net they are one source of VSTK, joined only to each other; and one of them in a net declares
    // its stacked partner through it (decision n757c07fe60e30e87), so the partner is no second source and joins that net's
    // expected group. A second such part left out of every net is still a second source and is refused, naming its pin.
    // A unit test on purpose: which created pins count as one source is isolated planning logic that the existing
    // common-pin test (APinCommonToAllUnitsIsOneSourceThatKiCadJoinsAcrossUnits) cannot express with its two-unit part; the
    // rendered creation journey creates such parts in KiCad with a declared connection and compares KiCad's own pin partition.
    [TestMethod]
    public void SameNamedHiddenPinsOneSymbolStacksAreOneConnectionWhenTheXmlAlsoAddsConnections()
    {
        var bench = new SchematicConnectionIntentBuilderTests.Bench();
        Guid r = bench.Part("R", SchematicConnectionIntentBuilderTests.Passive("1"), SchematicConnectionIntentBuilderTests.Passive("2"));
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2");
        var state = bench.State([]);
        static ConnectionPinKey[] Keys(SchematicDesign design, params (Guid Component, string Number)[] pins) => SchematicConnectionIntentBuilderTests.Keys(design, pins);
        var part = StackedPowerPart();
        DesignRecoveryState Saved(SchematicDesign design, params CircuitNet[] nets) =>
            SchematicConnectionIntentBuilderTests.Revise(state, _ => SchematicConnectionIntentBuilderTests.WithNets(design, nets)).Saved;

        // One part, U7. Unconnected, it is created as before, and its assertion joins its two VSTK pins only.
        var (single, singleIds) = WithCommonPowerPart(state.Baseline, part, "U7");
        Guid u7 = singleIds[0];
        var unconnected = SchematicSynchronizationPlanner.Plan(SchematicConnectionIntentBuilderTests.Revise(state, _ => single).Saved);
        Assert.IsTrue(unconnected.CanPrepare && unconnected.CandidateXml is not null, unconnected.ErrorCode + ": " + unconnected.ErrorMessage);
        // Guard: the XML adds OUT, joining U7.1 and R1.1, and leaves both VSTK pins in no net. They are one source of VSTK and
        // one expected group of their own.
        var alone = SchematicConnectionIntentBuilderTests.Plan(Saved(single, new CircuitNet(Guid.NewGuid(), "OUT", [new(u7, "1"), new(r1, "1")])));
        var aloneIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(alone);
        SchematicConnectionIntentBuilderTests.RequireGroups(aloneIntent, [Keys(alone.Candidate!, (u7, "1"), (r1, "1")), Keys(alone.Candidate!, (u7, "2"), (u7, "3"))]);

        // Two parts, U7 and U8.
        var (both, ids) = WithCommonPowerPart(state.Baseline, part, "U7", "U8");
        (u7, Guid u8) = (ids[0], ids[1]);
        var output = new CircuitNet(Guid.NewGuid(), "OUT", [new(u7, "1"), new(r1, "1")]);
        // Guard: VSTK holds U7.2 and U8.2 and leaves their stacked partners U7.3 and U8.3 out. Each partner is declared through
        // its listed pin, so nothing is refused, and all four pins are one expected group, the global net VSTK.
        var declared = SchematicConnectionIntentBuilderTests.Plan(Saved(both, new CircuitNet(Guid.NewGuid(), "VSTK", [new(u7, "2"), new(u8, "2")]), output));
        var declaredIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(declared);
        Assert.AreEqual("VSTK", declaredIntent.Nets.Single(n => n.Name == "VSTK").GlobalName);
        SchematicConnectionIntentBuilderTests.RequireGroups(declaredIntent, [Keys(declared.Candidate!, (u7, "2"), (u7, "3"), (u8, "2"), (u8, "3")),
            Keys(declared.Candidate!, (u7, "1"), (r1, "1")), Keys(declared.Candidate!, (u8, "1"))]);
        // Must-catch: VSTK holds U7.2 and R2.1 and leaves both of U8's VSTK pins out. U8's pair is a second source of VSTK, which
        // KiCad would join silently, so it is refused while planning, naming one of U8's pins (created pins are ordered by their
        // new identities) and the pin the XML lists in VSTK, never the partner declared only through it.
        var second = Saved(both, new CircuitNet(Guid.NewGuid(), "VSTK", [new(u7, "2"), new(r2, "1")]), output);
        SchematicConnectionIntentBuilderTests.RequireRefusal(second, SchematicConnectionErrors.ConnectedImplicitPowerConflict, "is named 'VSTK'");
        string refusal = SchematicConnectionIntentBuilderTests.Plan(second).ErrorMessage!;
        StringAssert.Matches(refusal, new System.Text.RegularExpressions.Regex(@"^Hidden power pin U8\.[23] is named 'VSTK'"));
        StringAssert.Matches(refusal, new System.Text.RegularExpressions.Regex(@"Add U8\.[23] to net 'VSTK', which holds U7\.2, so the connection is declared"));
    }

    /// <summary>A declared one-unit part: pin 1 OUT, passive and visible, and pins 2 and 3, both hidden power inputs named VSTK,
    /// drawn at one point 2.54 mm to its right.</summary>
    private static (PartDefinition Part, SchematicPartSymbol Declaration) StackedPowerPart()
    {
        var (declared, _) = SchematicPartSymbolTests.Fixture();
        var source = declared.PartSymbols!.Single();
        var part = new PartDefinition(Guid.NewGuid(), "Stacked power pins", 1, [new("1", "OUT", 1), new("2", "VSTK", 1), new("3", "VSTK", 1)]);
        var symbol = source.Symbol.Clone();
        symbol.CacheKey = "StackedPowerAlias";
        symbol.Definition.Id = new() { LibraryNickname = "Owned", EntryName = "StackedPowerDefinition" };
        symbol.Definition.UnitCount = 1;
        symbol.Definition.Items.Clear();
        foreach (var (number, type, x) in new[] { ("1", ElectricalPinType.EptPassive, 0L), ("2", ElectricalPinType.EptPowerInput, 2_540_000L),
            ("3", ElectricalPinType.EptPowerInput, 2_540_000L) })
            symbol.Definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = 1 }, Item = Any.Pack(new SchematicPin
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Number = number, Name = number == "1" ? "OUT" : "VSTK", ElectricalType = type,
                Visible = number == "1", Position = new() { XNm = x, YNm = 0 }
            }) });
        return (part, new SchematicPartSymbol(part.Id, new() { LibraryNickname = "External", EntryName = "StackedPower" }, symbol));
    }

    // CN-1 §5.5 for created local power symbols, and names KiCad resolves only when it builds the nets (review of ledger
    // pb41c5714361c378a): a new local power symbol that the XML leaves out of every net joins every label, hierarchical label
    // and local power symbol of its name on its own sheet, so it is refused while another exists there, and allowed on another
    // sheet or beside a global label of that name. An existing name with a text variable could be any created name, so a
    // created pin that could join it is refused. A unit test on purpose: these sources need sheets, labels and power symbols
    // that no single live fixture holds; the rendered creation journey proves the hidden power input case end to end.
    [TestMethod]
    public void UnconnectedLocalPowerNamesAndUnresolvedNamesAreRefusedBeforeKiCadJoinsThem()
    {
        var bench = new SchematicConnectionIntentBuilderTests.Bench();
        var root = SchematicConnectionIntentBuilderTests.BenchSheet.Root;
        var child = SchematicConnectionIntentBuilderTests.BenchSheet.Child;
        Guid local = bench.Part("LPWR", SchematicSymbolType.SstLocalPower,
            new SchematicConnectionIntentBuilderTests.BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        Guid power = bench.Part("PWR", SchematicSymbolType.SstGlobalPower,
            new SchematicConnectionIntentBuilderTests.BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        bench.Component(local, "#PWR1", value: "VL"); bench.Component(power, "#PWR2", value: "VG");
        bench.LocalLabel(root, "SIG"); bench.HierarchicalLabel(child, "H"); bench.GlobalLabel(root, "G");
        var state = bench.State([]);
        SchematicDesign NewLocal(string value, SchematicConnectionIntentBuilderTests.BenchSheet sheet, SchematicDesign? onto = null, string reference = "#PWR3") =>
            bench.Create(onto ?? state.Baseline, local, reference, sheet, value).Design;
        string Refused(SchematicDesign baseline, SchematicDesign desired, string code, string problem)
        {
            var error = Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(baseline, desired, []), problem);
            Assert.AreEqual(code, error.Code, problem + ": " + error.Message);
            StringAssert.Contains(error.Message, "nothing was changed", problem);
            var connected = Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(baseline, desired, [], allowConnected: true), problem);
            Assert.AreEqual((error.Code, error.Message), (connected.Code, connected.Message), problem + ": both modes apply the same rule.");
            return error.Message;
        }

        // Must-catch: a local power symbol, a label and a hierarchical label of the name on the same sheet, and two new ones.
        StringAssert.Contains(Refused(state.Baseline, NewLocal("VL", root), SchematicConnectionErrors.ConnectedNetNameConflict, "local power VL"),
            "Local power symbol #PWR3 is named 'VL', and KiCad joins it to every label, hierarchical label and local power symbol with that name on its sheet, "
            + "including local power symbol #PWR1, but the XML leaves the pin of power symbol #PWR3 out of every net. "
            + "Declare a net holding the pin of power symbol #PWR3 and the pin of power symbol #PWR1");
        StringAssert.Contains(Refused(state.Baseline, NewLocal("SIG", root), SchematicConnectionErrors.ConnectedNetNameConflict, "label SIG"),
            "including the label 'SIG'");
        StringAssert.Contains(Refused(state.Baseline, NewLocal("H", child), SchematicConnectionErrors.ConnectedNetNameConflict, "hierarchical label H"),
            "Add the pin of power symbol #PWR3 to the XML net whose pins the hierarchical label 'H' connects on that sheet");
        StringAssert.Contains(Refused(state.Baseline, NewLocal("VN", root, NewLocal("VN", root), "#PWR4"), SchematicConnectionErrors.ConnectedNetNameConflict,
            "two new local power symbols VN"), "including local power symbol #PWR4");
        // Guards: another sheet, a global label or global power symbol of the name, and one new symbol per sheet.
        _ = SchematicNativeCreationProjection.Project(state.Baseline, NewLocal("VL", child), []);
        _ = SchematicNativeCreationProjection.Project(state.Baseline, NewLocal("SIG", child), []);
        _ = SchematicNativeCreationProjection.Project(state.Baseline, NewLocal("G", root), []);
        _ = SchematicNativeCreationProjection.Project(state.Baseline, NewLocal("VG", root), []);
        _ = SchematicNativeCreationProjection.Project(state.Baseline, NewLocal("VN", root, NewLocal("VN", child), "#PWR4"), []);
        StringAssert.Contains(Refused(state.Baseline, NewLocal("${RAIL}", root), SchematicConnectionErrors.ConnectedPowerNameUnresolved, "created ${RAIL}"),
            "Power symbol #PWR3 is named '${RAIL}'");

        // Existing names with a text variable: a global power symbol named ${RAIL} and a label ${X} on the root sheet.
        var variables = new SchematicConnectionIntentBuilderTests.Bench();
        Guid localPart = variables.Part("LPWR", SchematicSymbolType.SstLocalPower,
            new SchematicConnectionIntentBuilderTests.BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        Guid powerPart = variables.Part("PWR", SchematicSymbolType.SstGlobalPower,
            new SchematicConnectionIntentBuilderTests.BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        variables.Component(powerPart, "#PWR1", value: "${RAIL}"); variables.Component(localPart, "#PWR2", value: "VL");
        variables.LocalLabel(root, "${X}");
        var unresolved = variables.State([]);
        StringAssert.Contains(Refused(unresolved.Baseline, variables.Create(unresolved.Baseline, powerPart, "#PWR3", root, "VCC").Design,
            SchematicConnectionErrors.ConnectedPowerNameUnresolved, "existing ${RAIL}"),
            "Power symbol #PWR1 is named '${RAIL}', which is empty or still contains a text variable, so KiCad may join it to power symbol #PWR3 ('VCC')");
        StringAssert.Contains(Refused(unresolved.Baseline, variables.Create(unresolved.Baseline, localPart, "#PWR3", root, "VN").Design,
            SchematicConnectionErrors.ConnectedPowerNameUnresolved, "existing ${X}"), "The label '${X}' is named '${X}'");
        // Guard: a local name elsewhere, which neither item can reach.
        _ = SchematicNativeCreationProjection.Project(unresolved.Baseline, variables.Create(unresolved.Baseline, localPart, "#PWR3", child, "VN").Design, []);
    }

    /// <summary>The declared two-unit part of <see cref="SchematicPartSymbolTests.Fixture"/> with its pin 1, VCC, common to both
    /// units, made a hidden power input, and the unit pins 2 and 3 passive and visible: drawn apart, or all at one point.</summary>
    private static (PartDefinition Part, SchematicPartSymbol Declaration) CommonPowerPart(bool stacked)
    {
        var (declared, _) = SchematicPartSymbolTests.Fixture();
        var source = declared.PartSymbols!.Single();
        var part = declared.Engineering.Circuit.Parts.Single(p => p.Id == source.PartId);
        Assert.AreEqual(0, part.Pins.Single(p => p.Number == "1").Unit, "Pin 1 is common to both units.");
        var symbol = source.Symbol.Clone();
        long x = 0;
        foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = child.Item.Unpack<SchematicPin>();
            (pin.ElectricalType, pin.Visible) = pin.Number == "1" ? (ElectricalPinType.EptPowerInput, false) : (ElectricalPinType.EptPassive, true);
            pin.Position = stacked ? new() : new() { XNm = x += 2_540_000, YNm = 0 };
            child.Item = Any.Pack(pin);
        }
        return (part, source with { Symbol = symbol });
    }

    /// <summary><paramref name="design"/> with one new component of <paramref name="declared"/> per reference on the root
    /// sheet, both units placed.</summary>
    private static (SchematicDesign Design, Guid[] Components) WithCommonPowerPart(SchematicDesign design,
        (PartDefinition Part, SchematicPartSymbol Declaration) declared, params string[] references)
    {
        var circuit = design.Engineering.Circuit;
        var root = circuit.SheetInstances.Single(s => s.ParentId is null);
        var definitions = references.Select(_ => new ComponentDefinition(Guid.NewGuid(), declared.Part.Id, "Common power part")).ToArray();
        var components = references.Select((reference, i) => new ComponentInstance(Guid.NewGuid(), definitions[i].Id, root.Id, reference)).ToArray();
        var occurrences = components.SelectMany((component, i) => Enumerable.Range(1, declared.Part.Units).Select(unit =>
            new SymbolOccurrence(Guid.NewGuid(), component.Id, unit, new SymbolPlacement(150 + 30 * i, 20 + 20 * unit, 0, false, false, false)))).ToArray();
        return (design with
        {
            PartSymbols = [.. design.PartSymbols ?? [], declared.Declaration],
            Engineering = design.Engineering with { Circuit = circuit with
            {
                Parts = [.. circuit.Parts, declared.Part],
                Sheets = [.. circuit.Sheets.Select(s => s.Id == root.DefinitionId ? s with { Components = [.. s.Components, .. definitions] } : s)],
                Components = [.. circuit.Components, .. components], Symbols = [.. circuit.Symbols, .. occurrences]
            } }
        }, [.. components.Select(c => c.Id)]);
    }

    // The pin partition an unconnected creation asserts (ledger pb41c5714361c378a, CN-1 §8.1): on the frozen PSU/CPU Components
    // stage it is exactly the fixture's expected native result, every pin alone except U2's stacked pins 1 and 4; on a repeated
    // sheet each channel instance's copy of a shared symbol's pin is its own group. A unit test of the helper the seam request
    // asks the executor to append; the rendered creation journey sends it to KiCad with a real creation.
    [TestMethod]
    public void CreationAssertionKeepsEveryCreatedPinAloneExceptStackedPins()
    {
        var (sheets, components) = PsuCpuComponents();
        var created = SchematicNativeCreationProjection.Project(sheets, components, []).Candidate;
        var operation = SchematicNativeCreationProjection.CreationAssertion(sheets, created);
        var assertion = operation.AssertConnectivity;
        Assert.AreEqual(1u, assertion.Version);
        var paths = created.Schematic.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)),
            s => s.Metadata.Document.SheetPath, StringComparer.Ordinal);
        var actual = assertion.ExpectedGroups.Select(g => g.Pins.Select(p =>
        {
            string path = string.Join('/', p.Path.Path.Select(id => id.Value));
            Assert.AreEqual(paths[path], p.Path, "Each pin names its exact loaded sheet instance.");
            return new ConnectionPinKey(path, Guid.Parse(p.Pin.Value));
        }).ToArray()).ToArray();
        var circuit = created.Engineering.Circuit;
        Guid Component(string reference) => circuit.Components.Single(c => c.Reference == reference).Id;
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Components);
        var groups = expected.JoinedPins.Select(g => SchematicConnectionIntentBuilderTests.Keys(created, [.. g.Select(p => (Component(p.Reference), p.Number))]))
            .Concat(expected.IsolatedPins.SelectMany(pair => pair.Value.SelectMany(number => SchematicConnectionIntentBuilderTests.Keys(created, (Component(pair.Key), number))))
                .Select(key => new[] { key })).ToArray();
        Assert.HasCount(1, expected.JoinedPins, "The Components stage joins only U2's stacked pins.");
        CollectionAssert.AreEquivalent(groups.Select(Text).ToArray(), actual.Select(Text).ToArray());
        // §5.8 order: keys by (sheet path, placed pin), groups by their first key.
        foreach (var group in actual)
            CollectionAssert.AreEqual(group.OrderBy(k => k.SheetPathKey, StringComparer.Ordinal).ThenBy(k => k.PlacedPinId).ToArray(), group);
        CollectionAssert.AreEqual(actual.OrderBy(g => g[0].SheetPathKey, StringComparer.Ordinal).ThenBy(g => g[0].PlacedPinId).Select(Text).ToArray(),
            actual.Select(Text).ToArray());
        Assert.AreEqual(operation, SchematicNativeCreationProjection.CreationAssertion(sheets, created), "The same creation asserts the same partition.");

        // A repeated channel sheet: the channel components share one physical symbol, whose pins are asserted once per instance.
        var repeated = Repeated((_, _, _) => null);
        var placed = SchematicNativeCreationProjection.Project(repeated.Baseline, repeated.Desired, [repeated.Library]).Candidate;
        var channel = SchematicNativeCreationProjection.CreationAssertion(repeated.Baseline, placed).AssertConnectivity;
        var keys = channel.ExpectedGroups.SelectMany(g => g.Pins).Select(p => string.Join('/', p.Path.Path.Select(id => id.Value)) + "|" + p.Pin.Value).ToArray();
        CollectionAssert.AllItemsAreUnique(keys);
        var expectedKeys = repeated.Components.SelectMany(c => repeated.Desired.Engineering.Circuit.Parts
                .Single(p => p.Id == repeated.Desired.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == c.DefinitionId).PartId).Pins
                .SelectMany(pin => SchematicConnectionIntentBuilderTests.Keys(placed, (c.Id, pin.Number))))
            .Select(k => k.SheetPathKey + "|" + k.PlacedPinId.ToString("D")).ToArray();
        CollectionAssert.AreEquivalent(expectedKeys, keys);
        var shared = placed.Engineering.Circuit.Symbols.Where(s => repeated.Components.Skip(1).Any(c => c.Id == s.ComponentId))
            .GroupBy(s => placed.SymbolBindings.Single(b => b.SymbolOccurrenceId == s.Id).NativeObjectId).ToArray();
        Assert.IsTrue(shared.All(g => g.Count() == 2), "Both channel components share each physical symbol.");

        static string Text(IEnumerable<ConnectionPinKey> group) => string.Join(",", group.Select(k => k.SheetPathKey + "/" + k.PlacedPinId.ToString("D")).Order(StringComparer.Ordinal));
    }

    // Placeholder target resolved by Repeated: the first channel sheet instance in UUID order.
    private static readonly Guid First = Guid.Parse("00000000-0000-4000-8000-00000000f125");

    private sealed record RepeatedCase(SchematicDesign Baseline, SchematicDesign Desired, ComponentKnowledgeLibrary Library,
        Guid Root, IReadOnlyList<ComponentInstance> Components);

    /// <summary>The declared two-unit part created once on the root sheet and once per repeated channel
    /// instance. <paramref name="place"/> maps (owning sheet, unit, root) to an occurrence sheet override;
    /// <see cref="First"/> stands for the first channel instance. Reusing <paramref name="like"/> keeps
    /// every identity, so two cases differ only in where units are placed.</summary>
    private static RepeatedCase Repeated(Func<Guid, int, Guid, Guid?> place, RepeatedCase? like = null)
    {
        var (declared, library) = like is null ? SchematicPartSymbolTests.Fixture() : (like.Desired, like.Library);
        var source = declared.PartSymbols!.Single();
        var circuit = like?.Baseline.Engineering.Circuit ?? declared.Engineering.Circuit;
        var part = declared.Engineering.Circuit.Parts.Single(p => p.Id == source.PartId);
        var baseline = like?.Baseline ?? declared with { PartSymbols = null, Engineering = declared.Engineering with
            { Circuit = circuit with { Parts = circuit.Parts.Where(p => p.Id != part.Id).ToArray() } } };
        circuit = baseline.Engineering.Circuit;
        var root = circuit.SheetInstances.Single(s => s.ParentId is null);
        var channels = circuit.SheetInstances.Where(s => s.ParentId == root.Id).OrderBy(s => s.Id).ToArray();
        var components = like?.Components.ToArray() ?? [.. new[] { root }.Concat(channels).Select((sheet, index) =>
            new ComponentInstance(Guid.NewGuid(), Guid.NewGuid(), sheet.Id, "U" + (31 + index)))];
        var rootDefinition = components[0].DefinitionId; var channelDefinition = components[1].DefinitionId;
        components = [.. components.Select(c => c.SheetInstanceId == root.Id ? c : c with { DefinitionId = channelDefinition })];
        var existing = like?.Desired.Engineering.Circuit.Symbols.Where(s => components.Any(c => c.Id == s.ComponentId))
            .ToDictionary(s => (s.ComponentId, s.Unit), s => s.Id);
        var symbols = components.SelectMany((component, index) => Enumerable.Range(1, part.Units).Select(unit =>
        {
            Guid? target = place(component.SheetInstanceId, unit, root.Id);
            if (target == First) target = channels[0].Id;
            // Units sharing one physical symbol share its placement; moved units get their own.
            decimal x = target is null ? (component.SheetInstanceId == root.Id ? 20 : 60) : 100 + 20 * index;
            return new SymbolOccurrence(existing?[(component.Id, unit)] ?? Guid.NewGuid(), component.Id, unit,
                new SymbolPlacement(x, 30 + 15 * unit, 0, false, false, false), target);
        })).ToArray();
        var desired = declared with { Engineering = declared.Engineering with { Circuit = circuit with
        {
            Parts = [.. circuit.Parts, part],
            Sheets = circuit.Sheets.Select(s => s.Id == root.DefinitionId ? s with { Components = [.. s.Components, new(rootDefinition, part.Id, "Root declared")] }
                : s.Id == channels[0].DefinitionId ? s with { Components = [.. s.Components, new(channelDefinition, part.Id, "Channel declared")] } : s).ToArray(),
            Components = [.. circuit.Components, .. components],
            Symbols = [.. circuit.Symbols, .. symbols]
        } } };
        return new(baseline, desired, library, root.Id, components);
    }

    internal static (SchematicDesign Baseline, SchematicDesign Desired) PsuCpuComponents()
    {
        Guid root = Guid.NewGuid();
        var baseline = PsuCpuFixture.Baseline(PsuCpuSheets(root), PsuCpuSeed.Sheets, root, CancellationToken.None);
        var engineering = PsuCpuFixture.Engineering(PsuCpuStage.Components);
        // Explicit grid placements: the rendered journeys measure real layout; this checks identity.
        var placed = engineering.Circuit.Symbols.Select((s, i) => s with { Placement = new SymbolPlacement(
            25.4m + 50.8m * (i % 5), 25.4m + 50.8m * (i / 5), 0, false, false, false) }).ToArray();
        return (baseline, baseline with { Engineering = engineering with { Circuit = engineering.Circuit with { Symbols = placed } },
            PartSymbols = PsuCpuDeclarations(engineering.Circuit) });
    }

    /// <summary>Validating declarations for the fixture parts, built offline from the frozen
    /// lib_symbols pins (numbers, units, body styles, K21 identities and definition positions) with
    /// standard fields. Native captures of the same pins are checked by the NativePsuCpuSeed journey.
    /// The exact positions keep the LP3982's pins 1 and 4 stacked at one point, as KiCad draws them.</summary>
    private static SchematicPartSymbol[] PsuCpuDeclarations(Circuit circuit)
    {
        string text = PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr");
        var pins = PsuCpuFixtureBuilder.LibraryPins(text);
        var positions = PsuCpuPinPositions(text);
        static SchematicField Field(string name, string text) => new() { Name = name,
            Text = new() { Text_ = text, Position = new(), Attributes = new() { Multiline = true } } };
        return [.. PsuCpuFixture.Parts().Where(p => circuit.Parts.Any(x => x.Id == p.Id)).Select(part =>
        {
            var names = circuit.Parts.Single(x => x.Id == part.Id).Pins.ToDictionary(p => (p.Number, p.Unit), p => p.Name);
            var own = pins.Where(p => p.CacheKey == part.CacheKey).ToArray();
            var definition = new SchematicSymbol
            {
                Id = new() { LibraryNickname = part.Library, EntryName = part.Entry }, UnitCount = (uint)part.Units,
                PinsUseLocalCoordinates = true, Type = SchematicSymbolType.SstNormal, EmbeddedFiles = new(),
                ReferenceField = Field("Reference", "U"), ValueField = Field("Value", part.Name),
                FootprintField = Field("Footprint", ""), DatasheetField = Field("Datasheet", ""), DescriptionField = Field("Description", "")
            };
            for (int style = 1; style <= Math.Max(1, own.Max(p => p.Style)); style++)
                definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Style " + style });
            foreach (var pin in own)
                definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = pin.Unit }, BodyStyle = new() { Style = pin.Style },
                    Item = Any.Pack(new SchematicPin { Id = new() { Value = pin.Id }, Number = pin.Number, Position = positions[pin.Id].Clone(),
                        Name = pin.Style is 0 or 1 ? names[(pin.Number, pin.Unit)] : pin.Name }) });
            return new SchematicPartSymbol(part.Id, new() { LibraryNickname = part.Library, EntryName = part.Entry },
                new() { CacheKey = part.CacheKey, Definition = definition, ShowPinNames = true, ShowPinNumbers = true,
                    PinNameOffset = new() { ValueNm = 508000 } });
        })];
    }

    /// <summary>Each definition pin's symbol-local position in lib_symbols.kicad_sexpr, by its K21 identity,
    /// in nanometres with KiCad's downward Y.</summary>
    internal static IReadOnlyDictionary<string, Kiapi.Common.Types.Vector2> PsuCpuPinPositions(string libSymbols)
    {
        var result = new Dictionary<string, Kiapi.Common.Types.Vector2>(StringComparer.Ordinal);
        foreach (var symbol in PsuCpuSexpr.Parse(libSymbols).Children("symbol"))
            foreach (var body in symbol.Children("symbol"))
                foreach (var pin in body.Children("pin"))
                {
                    var at = pin.Child("at");
                    static long Nm(string mm) => (long)decimal.Round(decimal.Parse(mm, System.Globalization.CultureInfo.InvariantCulture) * 1_000_000m);
                    result.Add(pin.Child("uuid").Value(1), new() { XNm = Nm(at.Value(1)), YNm = -Nm(at.Value(2)) });
                }
        return result;
    }

    /// <summary>The loaded S1 "Sheets" seed of contract §1.6.2 below a native-created root instance.</summary>
    private static SchematicHierarchyData PsuCpuSheets(Guid root) => PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets);

    internal static EngineeringDesign AddComponent(SchematicDesign baseline, bool coordinateFree = false)
    {
        var source = baseline.Engineering.Circuit.Components[0];
        var sourceDefinition = baseline.Engineering.Circuit.Sheets.SelectMany(s => s.Components)
            .Single(c => c.Id == source.DefinitionId);
        Guid sourceSheet = baseline.Engineering.Circuit.Sheets.Single(s => s.Components.Any(c => c.Id == source.DefinitionId)).Id;
        Guid definitionId = Guid.NewGuid();
        var instances = baseline.Engineering.Circuit.SheetInstances.Where(s => s.DefinitionId == sourceSheet).ToArray();
        var part = baseline.Engineering.Circuit.Parts.Single(p => p.Id == sourceDefinition.PartId);
        var components = instances.Select(_ => Guid.NewGuid()).ToArray();
        return baseline.Engineering with
        {
            Circuit = baseline.Engineering.Circuit with
            {
                Sheets = baseline.Engineering.Circuit.Sheets.Select(s => s.Id == sourceSheet
                    ? s with { Components = [.. s.Components, new(definitionId, sourceDefinition.PartId, "Created probe")] } : s).ToArray(),
                Components = [.. baseline.Engineering.Circuit.Components,
                    .. instances.Select((instance, index) => new ComponentInstance(components[index], definitionId, instance.Id, "U?"))],
                Symbols = [.. baseline.Engineering.Circuit.Symbols,
                    .. instances.SelectMany((instance, instanceIndex) => Enumerable.Range(1, part.Units)
                        .Select(unit => new SymbolOccurrence(Guid.NewGuid(), components[instanceIndex], unit,
                            coordinateFree ? null : new SymbolPlacement(50, 30 + (unit - 1) * 10,
                                0, false, false, false))))]
            }
        };
    }

    internal static (SchematicDesign Design, ComponentKnowledgeLibrary Library) Fixture()
    {
        var (design, library, _) = SchematicElectricalComparisonTests.Fixture();
        foreach (var screen in design.Schematic.Instances)
        for (int i = 0; i < screen.Items.Count; i++)
        {
            if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
            symbol.Position = new() { XNm = 10000000 + i * 1000000, YNm = 10000000 };
            symbol.Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 };
            symbol.Locked = LockedState.LsUnlocked;
            symbol.InstanceRecords = new();
            var record = new SymbolSheetRecord { ProjectName = screen.Metadata.Document.Project.Name,
                Reference = symbol.ReferenceField.Text.Text_, Unit = symbol.Unit.Unit, Variants = new() };
            record.Path.Add(screen.Metadata.Document.SheetPath.Path.Select(p => p.Clone()));
            symbol.InstanceRecords.Records.Add(record);
            screen.Items[i] = Any.Pack(symbol);
        }
        return (design, library);
    }
}
