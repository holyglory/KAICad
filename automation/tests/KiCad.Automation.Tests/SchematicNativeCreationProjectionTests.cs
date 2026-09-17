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
    }

    private static EngineeringDesign AddComponent(SchematicDesign baseline, bool coordinateFree = false)
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

    private static (SchematicDesign Design, ComponentKnowledgeLibrary Library) Fixture()
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
