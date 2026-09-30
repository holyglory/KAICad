using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicElectricalComparisonTests
{
    internal static (SchematicDesign Design, ComponentKnowledgeLibrary Library, SchematicElectricalState State) Fixture()
    {
        var (design, library) = SchematicDesignTests.Fixture();
        var ids = new Dictionary<(int Unit, string Number), string>();
        var libraryPins = design.Engineering.Circuit.Parts[0].Pins.ToDictionary(p => p.Number, _ => Guid.NewGuid().ToString("D"));
        foreach (var screen in design.Schematic.Instances)
        for (int i = 0; i < screen.Items.Count; ++i)
        {
            if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
            symbol.SeparatePinIdentities = true; symbol.Definition = new() { UnitCount = 2 };
            foreach (var pin in design.Engineering.Circuit.Parts[0].Pins.Where(p => p.Unit == 0 || p.Unit == symbol.Unit.Unit))
            {
                var key = (symbol.Unit.Unit, pin.Number);
                if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = Guid.NewGuid().ToString("D"));
                symbol.Definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(new SchematicPin
                    { Id = new() { Value = id }, LibraryPinId = new() { Value = libraryPins[pin.Number] }, Number = pin.Number, Name = pin.Name }) });
            }
            screen.Items[i] = Any.Pack(symbol);
        }
        var state = new SchematicElectricalState { Hierarchy = new() { Data = design.Schematic.Clone(),
            Revision = new() { Epoch = "electrical-epoch", Sequence = 42 }, TrackingComplete = false } };
        var net = new SchematicNet { Name = "Any display name" };
        foreach (var screen in state.Hierarchy.Data.Instances.Skip(1))
        {
            var sheet = new SchematicNetSheetContents { Path = screen.Metadata.Document.SheetPath.Clone() };
            foreach (var symbol in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()))
            foreach (var pin in symbol.Definition.Items.Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.Number == "8"))
                sheet.Items.Add(pin.Id.Clone());
            net.Sheets.Add(sheet);
        }
        state.Nets.Add(net);
        return (design, library, state);
    }

    [TestMethod]
    public void RepeatedSheetsAndMultiUnitPinsUseExactIdentitiesNotNames()
    {
        var f = Fixture(); byte[] before = f.State.ToByteArray();
        var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(result.PinBindingsComplete); Assert.IsTrue(result.ConnectivityEquivalent);
        Assert.IsEmpty(result.Differences); Assert.AreEqual(2, result.CoverageGaps.Count);
        CollectionAssert.AreEqual(before, f.State.ToByteArray());
        f.State.Nets[0].Name = "A renamed net, no identity change";
        Assert.IsTrue(SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]).ConnectivityEquivalent);
    }

    [TestMethod]
    public void SameNamedDisconnectedGroupsReportTheActualModelNetSplit()
    {
        var f = Fixture(); var original = f.State.Nets[0].Clone(); f.State.Nets.Clear();
        foreach (var sheet in original.Sheets)
        { var group = new SchematicNet { Name = "same-name" }; group.Sheets.Add(sheet.Clone()); f.State.Nets.Add(group); }
        var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(result.PinBindingsComplete); Assert.IsFalse(result.ConnectivityEquivalent);
        var split = result.Differences.Single(); Assert.AreEqual("model_net_split", split.Kind);
        Assert.AreEqual(f.Design.Engineering.Circuit.Nets[0].Id, split.ModelNetIds.Single());
        Assert.AreEqual(2, split.SnapshotNetIndexes.Count);
    }

    [TestMethod]
    public void JoiningAnUnassignedPinDoesNotPassAsTheSameNamedModelNet()
    {
        var f = Fixture(); var screen = f.State.Hierarchy.Data.Instances[1];
        var pin = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .SelectMany(s => s.Definition.Items.Select(c => c.Item.Unpack<SchematicPin>())).Single(p => p.Number == "1");
        f.State.Nets[0].Sheets[0].Items.Add(pin.Id.Clone());
        var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(result.PinBindingsComplete); Assert.IsFalse(result.ConnectivityEquivalent);
        Assert.IsTrue(result.Differences.Any(d => d.Kind == "native_net_join"));
    }

    [TestMethod]
    public void UnconnectedPinsRemainSeparateWithoutInventedNetIdentities()
    {
        var f = Fixture(); f.State.Nets.Clear();
        var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(result.PinBindingsComplete); Assert.IsFalse(result.ConnectivityEquivalent);
        Assert.IsEmpty(result.Differences.Single().SnapshotNetIndexes);
        var noModelConnections = f.Design with { Engineering = f.Design.Engineering with
        { Circuit = f.Design.Engineering.Circuit with { Nets = [] }, Structure = f.Design.Engineering.Structure with
            { Connections = f.Design.Engineering.Structure.Connections.Select(c => c with { NetIds = [] }).ToArray() } } };
        Assert.IsTrue(SchematicElectricalComparison.Compare(noModelConnections, f.State, [f.Library]).ConnectivityEquivalent);

        var placed = SchematicRebuildTests.Placed();
        var circuit = placed.Engineering.Circuit;
        var template = circuit.Components.Single(c => c.Reference == "R1");
        var ownerSheet = circuit.SheetInstances.Single(s => s.Id == template.SheetInstanceId);
        var partDefinition = circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == template.DefinitionId);
        var addedDefinition = partDefinition with { Id = Guid.NewGuid() };
        var undrawn = template with { Id = Guid.NewGuid(), DefinitionId = addedDefinition.Id, Reference = "R99" };
        var withUndrawn = placed with { Engineering = placed.Engineering with { Circuit = circuit with
        { Sheets = circuit.Sheets.Select(s => s.Id == ownerSheet.DefinitionId
            ? s with { Components = [.. s.Components, addedDefinition] } : s).ToArray(), Components = [.. circuit.Components, undrawn] } } };
        var native = SymbolSheetOwnershipTests.Isolated(placed.Schematic, new() { Epoch = "undrawn-fixture", Sequence = 1 });
        var accounted = SchematicElectricalComparison.Compare(withUndrawn, native, []);
        Assert.IsTrue(accounted.PinBindingsComplete && accounted.ConnectivityEquivalent);
        Assert.HasCount(2, accounted.UndrawnPins!.Where(p => p.Pin.ComponentId == undrawn.Id));
        Assert.IsTrue(accounted.PinPartitions!.Where(p => p.Pins.Any(pin => pin.ComponentId == undrawn.Id))
            .All(p => p.SnapshotNetIndex is null), "No native membership is invented for an undrawn pin.");
        var unknown = withUndrawn with { PartSymbols = withUndrawn.PartSymbols!.Where(p => p.PartId != partDefinition.PartId).ToArray() };
        Assert.IsFalse(SchematicElectricalComparison.Compare(unknown, native, []).PinBindingsComplete);
        var imaginedConnection = withUndrawn with { Engineering = withUndrawn.Engineering with { Circuit = withUndrawn.Engineering.Circuit with
        { Nets = [new CircuitNet(Guid.NewGuid(), "undrawn connection", [new(undrawn.Id, "1"), new(template.Id, "1")])] } } };
        Assert.IsFalse(SchematicElectricalComparison.Compare(imaginedConnection, native, []).ConnectivityEquivalent,
            "A declared symbol does not prove its undrawn connections exist.");
    }

    [TestMethod]
    public void UnknownDuplicateOrForeignMembershipsCannotProduceAnEquivalentResult()
    {
        foreach (string error in new[] { "unknown-item", "duplicate-membership", "foreign-sheet", "missing-identity", "wrong-unit", "empty-net", "empty-sheet", "empty-symbol-child" })
        {
            var f = Fixture();
            if (error == "unknown-item") f.State.Nets[0].Sheets[0].Items.Add(new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") });
            else if (error == "duplicate-membership") f.State.Nets.Add(f.State.Nets[0].Clone());
            else if (error == "foreign-sheet") f.State.Nets[0].Sheets[0].Path.Path[0].Value = Guid.NewGuid().ToString("D");
            else if (error == "empty-net") f.State.Nets[0].Sheets.Clear();
            else if (error == "empty-sheet") f.State.Nets[0].Sheets[0].Items.Clear();
            else
            {
                var screen = f.State.Hierarchy.Data.Instances[1];
                int index = screen.Items.ToList().FindIndex(i => i.Is(SchematicSymbolInstance.Descriptor));
                var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>();
                if (error == "missing-identity") symbol.SeparatePinIdentities = false;
                else if (error == "empty-symbol-child") symbol.Definition.Items.Add(new SchematicSymbolChild());
                else symbol.Unit.Unit = symbol.Unit.Unit == 1 ? 2 : 1;
                screen.Items[index] = Any.Pack(symbol);
            }
            var result = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
            Assert.IsFalse(result.PinBindingsComplete, error); Assert.IsFalse(result.ConnectivityEquivalent, error);
            Assert.IsNotEmpty(result.Issues, error);
        }
    }

    [TestMethod]
    public void WrongRootMissingRevisionAndCancellationFailBeforeComparison()
    {
        var f = Fixture();
        var wrong = f.State.Clone(); wrong.Hierarchy.Data.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
        Assert.ThrowsExactly<AutomationException>(() => SchematicElectricalComparison.Compare(f.Design, wrong, [f.Library]));
        wrong = f.State.Clone(); wrong.Hierarchy.Revision = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicElectricalComparison.Compare(f.Design, wrong, [f.Library]));
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library], new(true)));
    }
}
