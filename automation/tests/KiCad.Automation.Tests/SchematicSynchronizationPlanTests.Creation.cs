using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

public sealed partial class SchematicSynchronizationPlanTests
{
    [TestMethod]
    public void CreationOnlyXmlUsesExactNativeCreationOperationsAndDefersElectricalValidation()
    {
        var (baseline, library) = SchematicNativeCreationProjectionTests.Fixture();
        CompleteSharedRecords(baseline.Schematic);
        var source = baseline.Engineering.Circuit.Components[0];
        var sourceDefinition = baseline.Engineering.Circuit.Sheets.SelectMany(s => s.Components)
            .Single(c => c.Id == source.DefinitionId);
        var sourceSheet = baseline.Engineering.Circuit.Sheets.Single(s => s.Components.Any(c => c.Id == source.DefinitionId)).Id;
        var sheetInstances = baseline.Engineering.Circuit.SheetInstances.Where(s => s.DefinitionId == sourceSheet).ToArray();
        var definitionId = Guid.NewGuid();
        var componentIds = sheetInstances.Select(_ => Guid.NewGuid()).ToArray();
        var part = baseline.Engineering.Circuit.Parts.Single(p => p.Id == sourceDefinition.PartId);
        var symbols = sheetInstances.SelectMany((sheet, index) => Enumerable.Range(1, part.Units)
            .Select(unit => new SymbolOccurrence(Guid.NewGuid(), componentIds[index], unit,
                new SymbolPlacement(50, 30 + (unit - 1) * 10, 0, false, false, false)))).ToArray();
        var engineering = baseline.Engineering with
        {
            Circuit = baseline.Engineering.Circuit with
            {
                Sheets = baseline.Engineering.Circuit.Sheets.Select(sheet => sheet.Id == sourceSheet
                    ? sheet with { Components = [.. sheet.Components, new(definitionId, sourceDefinition.PartId, "Created probe")] }
                    : sheet).ToArray(),
                Components = [.. baseline.Engineering.Circuit.Components,
                    .. sheetInstances.Select((sheet, index) => new ComponentInstance(componentIds[index], definitionId, sheet.Id, "U?"))],
                Symbols = [.. baseline.Engineering.Circuit.Symbols, .. symbols]
            }
        };
        var desired = baseline with { Engineering = engineering };
        Assert.IsTrue(SchematicNativeCreationProjection.IsSupportedAddition(baseline, engineering), "creation-shape predicate rejected the fixture");
        var recovery = SchematicSynchronizationPlanTests.Fixture();
        var baselineElectrical = recovery.BaselineElectrical!.Clone();
        baselineElectrical.Hierarchy.Data = baseline.Schematic.Clone();
        var observedElectrical = baselineElectrical.Clone();
        var recoveryWithCreation = recovery with
        {
            Baseline = baseline,
            Observed = baseline.Schematic.Clone(),
            KnowledgeLibraries = [library],
            BaselineElectrical = baselineElectrical,
            ObservedElectrical = observedElectrical,
            DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, [library]))
        };
        Assert.IsTrue(SchematicNativeCreationProjection.IsSupportedAddition(recoveryWithCreation.Baseline,
            DesignRecoveryStore.ReadDesired(recoveryWithCreation).Engineering), "XML decoding changed the creation shape");
        var plan = SchematicSynchronizationPlanner.Plan(recoveryWithCreation);
        Assert.IsTrue(plan.CanPrepare, $"{plan.ErrorCode}: {plan.ErrorMessage}");
        Assert.IsTrue(plan.NativeConnectivityValidationRequired);
        Assert.IsNull(plan.ObservedConnectivity);
        Assert.AreEqual(symbols.Length, plan.Candidate!.Engineering.Circuit.Symbols.Count - baseline.Engineering.Circuit.Symbols.Count);
        Assert.AreEqual(symbols.Select(s => s.ComponentId).Distinct().Count(),
            plan.NativeOperations.Count(operation => operation.Create?.Is(Kiapi.Schematic.Types.SchematicSymbolInstance.Descriptor) == true));
        Assert.AreEqual(plan.CandidateXml, SchematicDesignXml.Write(SchematicDesignXml.Read(plan.CandidateXml!, [library]), [library]));
    }

}
