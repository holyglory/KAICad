using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

// Lane 2C cases of the synchronization seam (psu-cpu-fixture-and-ownership.md §2.5, ledger p74ee7c1da24272d9) that need the
// PSU/CPU Complete stage as a wired editor shows it, which only this partial class's wired-state helper builds. The native
// ownership journey removes units on the Components stage, which has no nets; this isolated case pins what a unit removal
// does to nets. SchematicRebuildTests could not be extended: it cannot reach the wired-state helper.
public sealed partial class SchematicSynchronizationPlanTests
{
    [TestMethod]
    public void XmlUnitRemovalTakesThePinsOnlyThatUnitDrawsOutOfTheirNets()
    {
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var typed = components with { PartSymbols = WithLibraryPinTypes(components.PartSymbols!) };
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;
        var (wired, state) = PsuCpuWired(WithPsuNets(SchematicNativeCreationProjection.Project(sheets, typed, []).Candidate, complete.Nets));
        Guid unitFour = PsuCpuIds.Id(0x09, 10), u5 = PsuCpuIds.Id(0x07, 7);
        string unitFourNative = wired.SymbolBindings.Single(b => b.SymbolOccurrenceId == unitFour).NativeObjectId.ToString("D");
        DesignRecoveryState Saving(SchematicDesign design) =>
            state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
        CircuitNet Net(SchematicDesign design, string name) => design.Engineering.Circuit.Nets.Single(n => n.Name == name);

        // The XML removes the processor's power unit and, with it, VDDIO (pin 3, RAIL_A) and VSS (pin 177, GND), the pins only
        // unit 4 draws. U5 keeps its other units and their pins in TELEM_MCU_TO_CPU, TELEM_CPU_TO_MCU and MEM_SCL.
        var removal = SchematicRebuildTests.WithoutOccurrences(wired, unitFour);
        CollectionAssert.DoesNotContain(Net(removal, "RAIL_A").Pins.ToArray(), new PinEndpoint(u5, "3"));
        CollectionAssert.DoesNotContain(Net(removal, "GND").Pins.ToArray(), new PinEndpoint(u5, "177"));
        var shape = SchematicRebuild.Classify(Saving(removal), removal);
        Assert.AreEqual(SchematicRebuildKind.Admitted, shape.Kind, shape.ErrorCode + ": " + shape.ErrorMessage);
        var plan = SchematicSynchronizationPlanner.Plan(Saving(removal));
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        CollectionAssert.AreEqual(new[] { unitFourNative }, plan.NativeOperations.Where(o => o.Remove is not null).Select(o => o.Remove.Value).ToArray(),
            "Only the unit's symbol is removed; the labels and wires it was drawn with stay as the person drew them.");
        Assert.IsTrue(plan.NativeOperations.Where(o => o.Remove is null).All(o => o.ReplaceLibraryCache is not null));
        CollectionAssert.AreEqual(new[] { unitFour }, plan.Electrical!.RemovedSymbolOccurrences!.ToArray());
        var candidate = plan.Candidate!.Engineering.Circuit;
        Assert.IsFalse(candidate.Nets.SelectMany(n => n.Pins).Any(p => p.ComponentId == u5 && p.Pin is "3" or "177"));
        foreach (var (net, pin) in new[] { ("TELEM_MCU_TO_CPU", "74"), ("TELEM_CPU_TO_MCU", "73"), ("MEM_SCL", "161") })
            CollectionAssert.Contains(candidate.Nets.Single(n => n.Name == net).Pins.ToArray(), new PinEndpoint(u5, pin), "Kept units keep their connections.");
        Assert.AreEqual(plan.CandidateXml, SchematicDesignXml.Write(plan.Candidate, state.KnowledgeLibraries));

        // Must-catch: XML that removes the unit but keeps its pins in their nets describes connections nothing draws. It is
        // refused by name before KiCad changes, and the XML is kept as written.
        var dangling = removal with { Engineering = removal.Engineering with { Circuit = removal.Engineering.Circuit with { Nets = wired.Engineering.Circuit.Nets } } };
        var refusal = SchematicRebuild.Classify(Saving(dangling), dangling);
        Assert.AreEqual(SchematicRebuildKind.Rejected, refusal.Kind);
        Assert.AreEqual(SchematicRebuild.UnitPinsConnected, refusal.ErrorCode);
        var refused = SchematicSynchronizationPlanner.Plan(Saving(dangling));
        Assert.AreEqual(SchematicRebuild.UnitPinsConnected, refused.ErrorCode);
        Assert.IsEmpty(refused.NativeOperations); Assert.IsNull(refused.CandidateXml);
        // Only one of the two kept: still refused.
        var half = removal with { Engineering = removal.Engineering with { Circuit = removal.Engineering.Circuit with { Nets =
            [.. removal.Engineering.Circuit.Nets.Select(n => n.Name == "GND" ? n with { Pins = [.. n.Pins, new PinEndpoint(u5, "177")] } : n)] } } };
        Assert.AreEqual(SchematicRebuild.UnitPinsConnected, SchematicRebuild.Classify(Saving(half), half).ErrorCode);

        // Must-not-claim: a removal that also drops a pin of a kept unit is another edit; it keeps the general path.
        var more = removal with { Engineering = removal.Engineering with { Circuit = removal.Engineering.Circuit with { Nets =
            [.. removal.Engineering.Circuit.Nets.Select(n => n.Name == "MEM_SCL" ? n with { Pins = [.. n.Pins.Where(p => p != new PinEndpoint(u5, "161"))] } : n)] } } };
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(Saving(more), more).Kind);
    }
}
