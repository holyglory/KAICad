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
            CollectionAssert.AreEqual(paths.Order(StringComparer.Ordinal).ToArray(), paths,
                "Native PackSymbol orders placement records by exact sheet path, not model occurrence UUID.");
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
