using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SymbolPinCoverageTests
{
    private static (SchematicDesign Design, ComponentKnowledgeLibrary Library, SchematicElectricalState State) Expanded()
    {
        var f = SchematicElectricalComparisonTests.Fixture();
        var pins = f.Design.Engineering.Circuit.Parts[0].Pins;
        var templates = f.State.Hierarchy.Data.Instances.SelectMany(s => s.Items)
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .SelectMany(s => s.Definition.Items.Select(i => i.Item.Unpack<SchematicPin>()))
            .GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.First());
        var identities = new Dictionary<(string Symbol, string Pin, int Style), string>();
        Edit(f.State.Hierarchy.Data, symbol =>
        {
            foreach (var child in symbol.Definition.Items)
            {
                var pin = child.Item.Unpack<SchematicPin>();
                child.Unit = new() { Unit = pins.Single(p => p.Number == pin.Number).Unit };
            }
            foreach (var declaration in pins.Where(p => p.Unit != 0 && p.Unit != symbol.Unit.Unit))
            {
                var pin = templates[declaration.Number].Clone(); pin.Id.Value = Identity(symbol.Id.Value, pin.Number, 1);
                symbol.Definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = declaration.Unit }, Item = Any.Pack(pin) });
            }
            var alternate = symbol.Definition.Items.First(c => c.Unit.Unit == symbol.Unit.Unit).Clone();
            var alternatePin = alternate.Item.Unpack<SchematicPin>(); alternatePin.Id.Value = Identity(symbol.Id.Value, alternatePin.Number, 2);
            alternate.BodyStyle = new() { Style = 2 }; alternate.Item = Any.Pack(alternatePin);
            symbol.Definition.Items.Add(alternate);
        });
        return (f.Design with { Schematic = f.State.Hierarchy.Data.Clone() }, f.Library, f.State);

        string Identity(string symbol, string pin, int style)
        {
            if (!identities.TryGetValue((symbol, pin, style), out var id)) identities.Add((symbol, pin, style), id = Guid.NewGuid().ToString("D"));
            return id;
        }
    }

    [TestMethod]
    public void InactiveUnitsAndBodyStylesRemainMetadataNotAdditionalPlacedPins()
    {
        var f = Expanded();
        var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(result.PinBindingsComplete, string.Join(',', result.Issues.Select(i => i.Code)));
        Assert.IsTrue(result.ConnectivityEquivalent); Assert.IsEmpty(result.UndrawnPins!);
        var screen = f.State.Hierarchy.Data.Instances[1];
        var symbol = screen.Items.First(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        var inactive = symbol.Definition.Items.First(c => c.Unit.Unit != 0 && c.Unit.Unit != symbol.Unit.Unit).Item.Unpack<SchematicPin>();
        f.State.Nets[0].Sheets.Single(s => s.Path.Equals(screen.Metadata.Document.SheetPath)).Items.Add(inactive.Id.Clone());
        var invalid = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsFalse(invalid.PinBindingsComplete); Assert.IsTrue(invalid.Issues.Any(i => i.Code == "net_item_not_in_snapshot"));
    }

    [TestMethod]
    public void UndrawnUnitPinsAreAccountedForWithoutInventedPlacementsOrNets()
    {
        var f = Undrawn();
        var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(result.PinBindingsComplete, string.Join(',', result.Issues.Select(i => i.Code)));
        Assert.IsTrue(result.ConnectivityEquivalent); Assert.AreEqual(2, result.UndrawnPins!.Count);
        foreach (var missing in result.UndrawnPins)
        {
            Assert.AreEqual(2, missing.Unit); Assert.AreEqual("7", missing.Pin.Pin); Assert.IsNotEmpty(missing.LibraryPinIds);
            var partition = result.PinPartitions!.Single(p => p.Pins.Contains(missing.Pin));
            Assert.IsNull(partition.SnapshotNetIndex); Assert.AreEqual(1, partition.Pins.Count);
        }
        var circuit = f.Design.Engineering.Circuit;
        var desired = f.Design with { Engineering = f.Design.Engineering with { Circuit = circuit with
            { Nets = circuit.Nets.Select(n => n with { Pins = [.. n.Pins, new(circuit.Components[0].Id, "7")] }).ToArray() } } };
        var disconnected = SchematicElectricalComparison.Compare(desired, f.State, [f.Library]);
        Assert.IsTrue(disconnected.PinBindingsComplete); Assert.IsFalse(disconnected.ConnectivityEquivalent);
    }

    [TestMethod]
    public void MissingActivePinsAndUnprovenUndrawnDeclarationsRemainFailures()
    {
        var active = Expanded();
        Edit(active.State.Hierarchy.Data, s =>
        {
            if (s.Unit.Unit != 2) return;
            foreach (var pin in s.Definition.Items.Where(c => c.Item.Unpack<SchematicPin>().Number == "7").ToArray()) s.Definition.Items.Remove(pin);
        });
        Assert.IsFalse(SchematicElectricalComparison.Compare(active.Design, active.State, [active.Library]).PinBindingsComplete);
        foreach (bool corruptIdentity in new[] { false, true })
        {
            var f = Undrawn();
            Edit(f.State.Hierarchy.Data, s =>
            {
                foreach (var item in s.Definition.Items.Where(c => c.Unit.Unit == 2).ToArray())
                {
                    if (!corruptIdentity) s.Definition.Items.Remove(item);
                    else
                    {
                        var pin = item.Item.Unpack<SchematicPin>(); pin.LibraryPinId.Value = "invalid"; item.Item = Any.Pack(pin);
                    }
                }
            });
            var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
            Assert.IsFalse(result.PinBindingsComplete); Assert.IsTrue(result.Issues.Any(i => i.Code == "unmapped_model_pin"));
        }
    }

    private static (SchematicDesign Design, ComponentKnowledgeLibrary Library, SchematicElectricalState State) Undrawn()
    {
        var f = Expanded();
        foreach (var screen in f.State.Hierarchy.Data.Instances)
        foreach (var item in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).ToArray())
        {
            var symbol = item.Unpack<SchematicSymbolInstance>(); if (symbol.Unit.Unit != 2) continue;
            var removed = symbol.Definition.Items.Select(c => c.Item.Unpack<SchematicPin>().Id.Value).ToHashSet();
            foreach (var sheet in f.State.Nets.SelectMany(n => n.Sheets).Where(s => s.Path.Equals(screen.Metadata.Document.SheetPath)))
                foreach (var pin in sheet.Items.Where(p => removed.Contains(p.Value)).ToArray()) sheet.Items.Remove(pin);
            screen.Items.Remove(item);
        }
        var kept = f.Design.Engineering.Circuit.Symbols.Where(s => s.Unit == 1).ToArray();
        var ids = kept.Select(s => s.Id).ToHashSet();
        var design = f.Design with { Engineering = f.Design.Engineering with { Circuit = f.Design.Engineering.Circuit with { Symbols = kept } },
            SymbolBindings = f.Design.SymbolBindings.Where(b => ids.Contains(b.SymbolOccurrenceId)).ToArray(), Schematic = f.State.Hierarchy.Data.Clone() };
        return (design, f.Library, f.State);
    }

    private static void Edit(SchematicHierarchyData hierarchy, Action<SchematicSymbolInstance> edit)
    {
        foreach (var screen in hierarchy.Instances)
        for (int i = 0; i < screen.Items.Count; i++)
        {
            if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>(); edit(symbol); screen.Items[i] = Any.Pack(symbol);
        }
    }
}
