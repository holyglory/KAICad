using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicPlacementPlanTests
{
    internal static (SchematicDesign Design, ComponentKnowledgeLibrary Library) Fixture()
    {
        var (design, library) = SchematicModelProjectionTests.Fixture();
        // The shared serialization fixture deliberately locks unit one. Movement
        // success cases require an unlocked baseline in both representations.
        Edit(design.Schematic, s => s.Locked = Kiapi.Common.Types.LockedState.LsUnlocked);
        var native = SchematicModelProjection.NativeSymbols(design, design.Schematic);
        design = design with { Engineering = design.Engineering with { Circuit = design.Engineering.Circuit with
        { Symbols = design.Engineering.Circuit.Symbols.Select(s => s with
            { Placement = SchematicModelProjection.Placement(native[s.Id]) }).ToArray() } } };
        return (design, library);
    }

    private static EngineeringDesign Change(SchematicDesign design, Func<SymbolOccurrence, SymbolPlacement?> change) =>
        design.Engineering with { Circuit = design.Engineering.Circuit with
        { Symbols = design.Engineering.Circuit.Symbols.Select(s => s with { Placement = change(s) }).ToArray() } };

    [TestMethod]
    public void ExistingNativeLocksRejectMovementWithoutPartialProposals()
    {
        var (baseline, library) = Fixture();
        Edit(baseline.Schematic, s => s.Locked = Kiapi.Common.Types.LockedState.LsLocked);
        baseline = baseline with { Engineering = Change(baseline, s => s.Placement! with { Locked = true }) };
        var desired = Change(baseline, s => s.Placement! with { XMillimeters = s.Placement.XMillimeters + 1 });
        var result = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
        Assert.AreEqual(baseline.Engineering.Circuit.Symbols.Count,
            result.Issues.Count(i => i.Code == "locked_symbol"));
        Assert.AreEqual(0, result.Operations.Count);
        var unchanged = SchematicPlacementPlan.Plan(baseline, baseline.Engineering, baseline.Schematic, [library]);
        Assert.AreEqual(0, unchanged.Issues.Count);
        Assert.AreEqual(0, unchanged.Operations.Count);
    }

    [TestMethod]
    public void ToolPreservesProposalsAndReportsParsingFailureRecoveryAndCancellation()
    {
        var (baseline, library) = Fixture();
        var desired = Change(baseline, s => s.Placement! with { XMillimeters = s.Placement.XMillimeters + 1 });
        string xml = SchematicDesignXml.Write(baseline, [library]);
        string desiredXml = EngineeringDesignXml.Write(desired, [library]);
        string observed = SchematicDataXml.Write(baseline.Schematic);
        string[] libraries = [ComponentKnowledgeXml.WriteLibrary(library)];
        var tools = new PlacementTools();
        var invalid = tools.PlanPlacement(xml, desiredXml, SchematicDataXml.Write(new SchematicText()), libraries, default);
        Assert.AreEqual("invalid_design_xml", invalid.ErrorCode);
        Assert.AreEqual(0, invalid.Moves.Count); Assert.IsNull(invalid.ReconciledEngineeringXml);
        var missing = tools.PlanPlacement(xml, desiredXml, observed, [], default);
        Assert.IsNotNull(missing.ErrorCode); Assert.AreEqual(0, missing.Moves.Count);
        Assert.ThrowsExactly<OperationCanceledException>(() => tools.PlanPlacement(xml, desiredXml, observed, libraries, new(true)));
        var result = tools.PlanPlacement(xml, desiredXml, observed, libraries, default);
        Assert.IsNull(result.ErrorCode); Assert.AreEqual(0, result.Issues.Count);
        Assert.AreEqual(1, result.Moves.Count); Assert.AreEqual(2, result.Moves[0].SymbolIds.Count);
        Assert.AreEqual(1000000L, result.Moves[0].DeltaXNm);
        Assert.AreEqual(desiredXml, result.ReconciledEngineeringXml);
        Assert.IsTrue(result.CoverageGaps.Count > 0);
        var document = SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(result.Moves[0].DocumentJson);
        Assert.AreEqual(2, document.SheetPath.Path.Count);
    }

    [TestMethod]
    public void SharedNativeGeometryMovesOnceWithExactTargetAndPreservedIntent()
    {
        var (baseline, library) = Fixture();
        string before = SchematicDesignXml.Write(baseline, [library]);
        var desired = Change(baseline, s => s.Placement! with { XMillimeters = s.Placement.XMillimeters + 2.54m });
        desired = desired with { Structure = desired.Structure with { Statements = desired.Structure.Statements
            .Select(s => s with { Text = s.Text + "\nKeep this placement preference." }).ToArray() } };
        var result = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
        Assert.AreEqual(0, result.Issues.Count);
        Assert.AreEqual(1, result.Operations.Count, "Shared units with the same displacement must move as one native selection.");
        foreach (var operation in result.Operations)
        {
            Assert.IsNotNull(operation.TargetDocument);
            Assert.AreEqual(2, operation.TargetDocument.SheetPath.Path.Count);
            Assert.AreEqual(2540000L, operation.MoveConnectedSymbols.Delta.XNm);
            Assert.AreEqual(0L, operation.MoveConnectedSymbols.Delta.YNm);
            Assert.AreEqual(2, operation.MoveConnectedSymbols.Symbols.Count);
            Assert.AreEqual(2, operation.MoveConnectedSymbols.Symbols.Select(s => s.Value).Distinct().Count());
        }
        Assert.AreEqual(EngineeringDesignXml.Write(desired, [library]), EngineeringDesignXml.Write(result.ReconciledModel!, [library]));
        Assert.AreEqual(before, SchematicDesignXml.Write(baseline, [library]));
        Assert.IsTrue(result.CoverageGaps.Count > 0, "A translation plan cannot erase native coverage gaps.");
    }

    [TestMethod]
    public void DifferentDisplacementsRemainSeparateNativeSelections()
    {
        var (baseline, library) = Fixture();
        var desired = Change(baseline, s => s.Placement! with
            { XMillimeters = s.Placement.XMillimeters + s.Unit });
        var result = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
        Assert.AreEqual(0, result.Issues.Count);
        Assert.AreEqual(2, result.Operations.Count);
        CollectionAssert.AreEqual(new long[] { 1000000, 2000000 },
            result.Operations.Select(o => o.MoveConnectedSymbols.Delta.XNm).Order().ToArray());
        foreach (var operation in result.Operations)
            Assert.AreEqual(1, operation.MoveConnectedSymbols.Symbols.Count);
    }

    [TestMethod]
    public void InputOrderingCannotChangeNativeTargetsOrSelectionOrdering()
    {
        var (baseline, library) = Fixture();
        var desired = Change(baseline, s => s.Placement! with
            { YMillimeters = s.Placement.YMillimeters + 2.54m });
        var expected = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
        var reordered = baseline with
        {
            Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
                { Symbols = baseline.Engineering.Circuit.Symbols.Reverse().ToArray() } },
            SheetBindings = baseline.SheetBindings.Reverse().ToArray(),
            SymbolBindings = baseline.SymbolBindings.Reverse().ToArray()
        };
        desired = desired with { Circuit = desired.Circuit with { Symbols = desired.Circuit.Symbols.Reverse().ToArray() } };
        var observed = baseline.Schematic.Clone();
        var instances = observed.Instances.Reverse().ToArray();
        observed.Instances.Clear(); observed.Instances.Add(instances);
        var actual = SchematicPlacementPlan.Plan(reordered, desired, observed, [library]);
        Assert.AreEqual(0, actual.Issues.Count);
        CollectionAssert.AreEqual(expected.Operations.ToArray(), actual.Operations.ToArray());
    }

    [TestMethod]
    public void RepeatedInstancesCannotRequestDifferentGeometry()
    {
        var (baseline, library) = Fixture(); var first = baseline.Engineering.Circuit.Symbols[0];
        var desired = Change(baseline, s => s.Id == first.Id ? s.Placement! with { XMillimeters = s.Placement.XMillimeters + 1 } : s.Placement);
        var result = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
        Assert.IsTrue(result.Issues.Any(i => i.Code == "shared_placement_conflict"));
        Assert.AreEqual(0, result.Operations.Count);
    }

    [TestMethod]
    public void MissingCoordinatesAndAlreadyAppliedMovesDoNotCauseAdditionalEdits()
    {
        var (baseline, library) = Fixture();
        var coordinateFree = baseline with { Engineering = Change(baseline, _ => null) };
        var unchanged = SchematicPlacementPlan.Plan(coordinateFree, coordinateFree.Engineering, baseline.Schematic, [library]);
        Assert.AreEqual(0, unchanged.Operations.Count); Assert.AreEqual(0, unchanged.Issues.Count);
        var desired = Change(baseline, s => s.Placement! with { XMillimeters = s.Placement.XMillimeters + 1 });
        var observed = baseline.Schematic.Clone();
        Edit(observed, s => s.Position.XNm += 1000000);
        var applied = SchematicPlacementPlan.Plan(baseline, desired, observed, [library]);
        Assert.AreEqual(0, applied.Operations.Count); Assert.AreEqual(0, applied.Conflicts.Count);
        Assert.AreEqual(0, applied.Issues.Count);
    }

    [TestMethod]
    public void ConflictingNativeMoveHasNoPartialPlan()
    {
        var (baseline, library) = Fixture();
        var desired = Change(baseline, s => s.Placement! with { XMillimeters = s.Placement.XMillimeters + 1 });
        var observed = baseline.Schematic.Clone(); Edit(observed, s => s.Position.XNm += 2000000);
        var result = SchematicPlacementPlan.Plan(baseline, desired, observed, [library]);
        Assert.IsTrue(result.Conflicts.Count > 0); Assert.AreEqual(0, result.Operations.Count);
    }

    [TestMethod]
    public void LocksAndUnalignedBaselinesAreNotSilentlyTranslated()
    {
        var (baseline, library) = Fixture();
        foreach (var desired in new[] { Change(baseline, s => s.Placement! with { Locked = true }) })
        {
            var result = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
            Assert.IsTrue(result.Issues.Any(i => i.Code == "placement_lock_change"));
            Assert.AreEqual(0, result.Operations.Count);
        }
        var badBaseline = baseline with { Engineering = Change(baseline, s => s.Placement! with { XMillimeters = 12 }) };
        var mismatch = SchematicPlacementPlan.Plan(badBaseline, badBaseline.Engineering, baseline.Schematic, [library]);
        Assert.IsTrue(mismatch.Issues.Any(i => i.Code == "unaligned_placement_baseline"));
        Assert.AreEqual(0, mismatch.Operations.Count);
    }

    [TestMethod]
    public void SharedTransformsPrecedeGroupedTranslationsAndEquivalentEncodingDoesNotMove()
    {
        var (baseline, library) = Fixture();
        var desired = Change(baseline, s => s.Placement! with
        { RotationDegrees = (s.Placement.RotationDegrees + 90) % 360, XMillimeters = s.Placement.XMillimeters + 2.54m });
        var plan = SchematicPlacementPlan.Plan(baseline, desired, baseline.Schematic, [library]);
        Assert.IsEmpty(plan.Issues);
        var transforms = plan.Operations.TakeWhile(o => o.TransformConnectedSymbols is not null).ToArray();
        Assert.AreEqual(2, transforms.Length);
        Assert.IsTrue(plan.Operations.Skip(transforms.Length).All(o => o.MoveConnectedSymbols is not null));
        Assert.AreEqual(2, transforms.SelectMany(o => o.TransformConnectedSymbols.Symbols).Select(s => s.Value).Distinct().Count());
        var proposal = new PlacementTools().PlanPlacement(SchematicDesignXml.Write(baseline, [library]),
            EngineeringDesignXml.Write(desired, [library]), SchematicDataXml.Write(baseline.Schematic),
            [ComponentKnowledgeXml.WriteLibrary(library)], default);
        Assert.IsNull(proposal.ErrorCode); Assert.AreEqual(transforms.Length, proposal.Transforms!.Count);
        Assert.AreEqual(plan.Operations.Count - transforms.Length, proposal.Moves.Count);
        var equivalent = Change(baseline, s => s.Placement! with
        { RotationDegrees = (s.Placement.RotationDegrees + 180) % 360, MirrorX = !s.Placement.MirrorX, MirrorY = !s.Placement.MirrorY });
        Assert.IsEmpty(SchematicPlacementPlan.Plan(baseline, equivalent, baseline.Schematic, [library]).Operations);
        var aliasBaseline = baseline with { Engineering = equivalent };
        var unchanged = SchematicPlacementPlan.Plan(aliasBaseline, equivalent, baseline.Schematic, [library]);
        Assert.IsEmpty(unchanged.Issues); Assert.IsEmpty(unchanged.Operations);
    }

    [TestMethod]
    public void NativeRangeCoverageRemainderAndCancellationRemainExplicit()
    {
        var (baseline, library) = Fixture();
        var tooFar = Change(baseline, s => s.Placement! with { XMillimeters = 214748.3648m });
        var invalid = SchematicPlacementPlan.Plan(baseline, tooFar, baseline.Schematic, [library]);
        Assert.IsTrue(invalid.Issues.Any(i => i.Code == "native_coordinate_range"));
        Assert.AreEqual(0, invalid.Operations.Count);
        var observed = baseline.Schematic.Clone();
        Edit(observed, s => s.PinMapOverride = new() { Mode = (PinMapOverrideMode)2, ActiveMapName = "routing-swap" });
        var remainder = SchematicPlacementPlan.Plan(baseline, baseline.Engineering, observed, [library]);
        Assert.IsTrue(remainder.UnprojectedSnapshotChanges);
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicPlacementPlan.Plan(baseline,
            baseline.Engineering, observed, [library], new(true)));
    }

    private static void Edit(SchematicHierarchyData data, Action<SchematicSymbolInstance> edit)
    {
        foreach (var screen in data.Instances)
        for (int index = 0; index < screen.Items.Count; index++)
        {
            if (!screen.Items[index].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>(); edit(symbol);
            screen.Items[index] = Any.Pack(symbol);
        }
    }
}
