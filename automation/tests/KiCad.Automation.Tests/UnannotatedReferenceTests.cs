using System.Text.Json;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UnannotatedReferenceTests
{
    [TestMethod, DataRow("U?"), DataRow("R?"), DataRow("?")]
    public void DuplicateNativePlaceholdersKeepDistinctComponentAndPinIdentities(string reference)
    {
        var original = CircuitXmlTests.Fixture();
        var circuit = original with
        {
            Components = original.Components.Select(c => c with { Reference = reference }).ToArray()
        };

        string xml = CircuitXml.Write(circuit);
        var read = CircuitXml.Read(xml);
        Assert.AreEqual(xml, CircuitXml.Write(read));
        Assert.HasCount(2, read.Components);
        Assert.AreNotEqual(read.Components[0].Id, read.Components[1].Id);
        Assert.IsTrue(read.Components.All(c => c.Reference == reference));
        CollectionAssert.AreEquivalent(original.Nets.Single().Pins.ToArray(), read.Nets.Single().Pins.ToArray());
        CollectionAssert.AreEquivalent(original.Symbols.Select(s => s.Id).ToArray(), read.Symbols.Select(s => s.Id).ToArray());
    }

    [TestMethod, DataRow("U1"), DataRow("R1"), DataRow("U?1"), DataRow("U")]
    public void AssignedDuplicateReferencesStillFailWithoutChangingTheInput(string reference)
    {
        var original = CircuitXmlTests.Fixture();
        string before = CircuitXml.Write(original);
        var duplicate = original with
        {
            Components = original.Components.Select(c => c with { Reference = reference }).ToArray()
        };

        Assert.AreEqual("invalid_circuit", Assert.ThrowsExactly<AutomationException>(() => CircuitXml.Write(duplicate)).Code);
        Assert.AreEqual(before, CircuitXml.Write(original));
    }

    [TestMethod]
    public void PlaceholderSnapshotRoundTripAndNativeAnnotationUseExactBindings()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var original = state.Baseline;
        var circuit = original.Engineering.Circuit with
        {
            Components = original.Engineering.Circuit.Components.Select(c => c with { Reference = "U?" }).ToArray()
        };
        var unannotated = SchematicPropertyProjection.Project(original,
            original with { Engineering = original.Engineering with { Circuit = circuit } }, state.KnowledgeLibraries);
        var initial = SchematicDesignBindings.Inspect(unannotated, state.KnowledgeLibraries);
        Assert.IsTrue(initial.IdentitiesResolved);
        Assert.IsEmpty(initial.Differences);

        string xml = SchematicDesignXml.Write(unannotated, state.KnowledgeLibraries);
        var reconstructed = SchematicDesignXml.Read(xml, state.KnowledgeLibraries);
        Assert.AreEqual(xml, SchematicDesignXml.Write(reconstructed, state.KnowledgeLibraries));
        CollectionAssert.AreEquivalent(original.SymbolBindings.ToArray(), reconstructed.SymbolBindings.ToArray());
        var electrical = state.ObservedElectrical!.Clone();
        electrical.Hierarchy.Data = reconstructed.Schematic.Clone();
        Assert.IsTrue(SchematicElectricalComparison.Compare(reconstructed, electrical, state.KnowledgeLibraries).ConnectivityEquivalent);

        var assigned = reconstructed.Engineering with
        {
            Circuit = reconstructed.Engineering.Circuit with
            {
                Components = reconstructed.Engineering.Circuit.Components
                    .Select((c, i) => c with { Reference = "U" + (i + 100) }).ToArray()
            }
        };
        var annotated = SchematicPropertyProjection.Project(reconstructed,
            reconstructed with { Engineering = assigned }, state.KnowledgeLibraries);
        var reverse = SchematicModelProjection.Reconcile(reconstructed, reconstructed.Engineering,
            annotated.Schematic, state.KnowledgeLibraries);
        Assert.IsNotNull(reverse.Candidate, reverse.ErrorMessage);
        Assert.AreEqual(CircuitXml.Write(assigned.Circuit), CircuitXml.Write(reverse.Candidate.Circuit));
        Assert.AreEqual(JsonSerializer.Serialize(original.Engineering.Structure.Blocks.OrderBy(x => x.Id)),
            JsonSerializer.Serialize(reverse.Candidate.Structure.Blocks.OrderBy(x => x.Id)));
        Assert.AreEqual(JsonSerializer.Serialize(original.Engineering.Structure.Ports.OrderBy(x => x.Id)),
            JsonSerializer.Serialize(reverse.Candidate.Structure.Ports.OrderBy(x => x.Id)));
        Assert.AreEqual(JsonSerializer.Serialize(original.Engineering.Structure.Connections.OrderBy(x => x.Id)),
            JsonSerializer.Serialize(reverse.Candidate.Structure.Connections.OrderBy(x => x.Id)));
        Assert.AreEqual(JsonSerializer.Serialize(original.Engineering.Structure.Statements.OrderBy(x => x.Id)),
            JsonSerializer.Serialize(reverse.Candidate.Structure.Statements.OrderBy(x => x.Id)));

        var undone = SchematicModelProjection.Reconcile(annotated, annotated.Engineering,
            reconstructed.Schematic, state.KnowledgeLibraries);
        Assert.IsNotNull(undone.Candidate, undone.ErrorMessage);
        Assert.IsTrue(undone.Candidate.Circuit.Components.All(c => c.Reference == "U?"));
        CollectionAssert.AreEquivalent(original.Engineering.Circuit.Nets.Single().Pins.ToArray(),
            undone.Candidate.Circuit.Nets.Single().Pins.ToArray());
        foreach (var screen in reconstructed.Schematic.Instances)
        foreach (var symbol in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                     .Select(i => i.Unpack<SchematicSymbolInstance>()))
            Assert.AreEqual("U?", symbol.ReferenceField.Text.Text_);
    }
}
