using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicFieldLayoutPlannerTests
{
    private static SchematicFieldPlacement Move(Guid id) => new(new(id, SchematicFieldSlot.Reference, "Reference"),
        40_000_000, 45_000_000, 90, HorizontalAlignment.HaLeft, VerticalAlignment.VaBottom);

    [TestMethod]
    public void SnapshotComparisonIgnoresEnumerationButDetectsRealFieldChanges()
    {
        var state = Fixture(); var before = state.Baseline.Schematic; var reordered = before.Clone();
        var screens = reordered.Instances.Reverse().ToArray(); reordered.Instances.Clear(); reordered.Instances.Add(screens);
        foreach (var screen in reordered.Instances)
        {
            var items = screen.Items.Reverse().ToArray(); screen.Items.Clear(); screen.Items.Add(items);
        }
        Assert.AreNotEqual(before, reordered);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(before, reordered));
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(reordered, before));
        foreach (var screen in reordered.Instances)
        for (int i = 0; i < screen.Items.Count; i++)
        {
            if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
            symbol.ReferenceField.Text.Position.XNm += 100;
            screen.Items[i] = Any.Pack(symbol);
        }
        Assert.IsNotEmpty(SchematicHierarchyDelta.Plan(before, reordered));
        Assert.IsNotEmpty(SchematicHierarchyDelta.Plan(reordered, before));
    }

    [TestMethod]
    public void MovesOnePhysicalFieldAcrossAliasesWithoutChangingTextOrCircuit()
    {
        var state = Fixture(); var id = state.Baseline.Engineering.Circuit.Symbols[0].Id;
        string before = SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries);
        var result = SchematicFieldLayoutPlanner.Propose(state, [Move(id)]);
        Assert.IsNotEmpty(result.Operations); Assert.IsTrue(result.RequiresRenderedReview);
        Assert.AreEqual(before, SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries));
        Assert.AreEqual(EngineeringDesignXml.Write(state.Baseline.Engineering, state.KnowledgeLibraries),
            EngineeringDesignXml.Write(result.Candidate.Engineering, state.KnowledgeLibraries));
        var oldSymbols = SchematicModelProjection.NativeSymbols(state.Baseline, state.Baseline.Schematic);
        var newSymbols = SchematicModelProjection.NativeSymbols(result.Candidate, result.Candidate.Schematic);
        Guid physical = state.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == id).NativeObjectId;
        foreach (var (occurrence, symbol) in newSymbols)
        {
            var old = oldSymbols[occurrence];
            bool affected = state.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence).NativeObjectId == physical;
            if (!affected) { Assert.AreEqual(old, symbol); continue; }
            Assert.AreEqual(Move(id).XNm, symbol.ReferenceField.Text.Position.XNm);
            Assert.AreEqual(90, symbol.ReferenceField.Text.Attributes.Angle.ValueDegrees);
            Assert.AreEqual(old.ReferenceField.Text.Text_, symbol.ReferenceField.Text.Text_);
            Assert.AreEqual(old.ValueField, symbol.ValueField);
            Assert.AreEqual(old.Position, symbol.Position); Assert.AreEqual(old.Transform, symbol.Transform);
            Assert.AreEqual(old.Definition, symbol.Definition); Assert.AreEqual(old.InstanceRecords, symbol.InstanceRecords);
            Assert.IsFalse(symbol.FieldsAutoplaced);
            Assert.IsTrue(result.AffectedSymbols.Contains(occurrence));
        }
        var plan = SchematicSynchronizationPlanner.Plan(state with { DesiredFileBytes = Encoding.UTF8.GetBytes(result.DesiredXml) });
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage);
        var settled = Rebase(state, result.Candidate);
        var unchanged = SchematicFieldLayoutPlanner.Propose(settled, [Move(id)]);
        Assert.IsEmpty(unchanged.Operations); Assert.IsEmpty(unchanged.AffectedSymbols);
        Assert.AreEqual(result.DesiredXml, unchanged.DesiredXml);
    }

    [TestMethod]
    public void DuplicateAddressesConflictingAliasesAndMissingFieldsCannotProducePartialEdits()
    {
        var state = Fixture(); var id = state.Baseline.Engineering.Circuit.Symbols[0].Id; var move = Move(id);
        Guid native = state.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == id).NativeObjectId;
        Guid alias = state.Baseline.SymbolBindings.First(b => b.NativeObjectId == native && b.SymbolOccurrenceId != id).SymbolOccurrenceId;
        SchematicFieldPlacement[][] invalid =
        [
            [move, move],
            [move, Move(alias) with { XNm = move.XNm + 100 }],
            [move with { Field = move.Field with { ExpectedName = "Renamed" } }],
            [move with { Field = move.Field with { SymbolOccurrenceId = Guid.NewGuid() } }],
            [move with { Field = move.Field with { Slot = SchematicFieldSlot.User, UserIndex = 99 } }],
            [move with { Field = move.Field with { UserIndex = 0 } }],
            [move with { XNm = 101 }],
            [move with { YNm = long.MaxValue }],
            [move with { RotationDegrees = 45 }],
            [move with { HorizontalAlignment = HorizontalAlignment.HaUnknown }],
            [move with { VerticalAlignment = VerticalAlignment.VaIndeterminate }]
        ];
        byte[] before = state.DesiredFileBytes.ToArray();
        foreach (var changes in invalid)
        {
            Assert.ThrowsExactly<AutomationException>(() => SchematicFieldLayoutPlanner.Propose(state, changes));
            CollectionAssert.AreEqual(before, state.DesiredFileBytes);
        }
        var matching = SchematicFieldLayoutPlanner.Propose(state, [move, Move(alias)]);
        Assert.IsNotEmpty(matching.Operations);
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicFieldLayoutPlanner.Propose(state, [move], new(true)));
    }

    [TestMethod]
    public void LockedSymbolAndContainingGroupArePreserved()
    {
        foreach (bool group in new[] { false, true })
        {
            var state = Fixture(); Guid id = state.Baseline.Engineering.Circuit.Symbols[0].Id;
            Guid native = state.Baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == id).NativeObjectId;
            var design = state.Baseline with { Schematic = state.Baseline.Schematic.Clone() };
            foreach (var screen in design.Schematic.Instances)
            for (int i = 0; i < screen.Items.Count; i++)
            {
                if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                if (symbol.Id.Value != native.ToString("D")) continue;
                if (group)
                {
                    var owner = new Group { Id = new() { Value = "427ccbbd-03c8-4784-8bcb-8847c8a70517" }, Locked = LockedState.LsLocked };
                    owner.Items.Add(symbol.Id.Clone()); screen.Items.Add(Any.Pack(owner));
                }
                else
                {
                    symbol.Locked = LockedState.LsLocked; screen.Items[i] = Any.Pack(symbol);
                    design = design with { Engineering = design.Engineering with { Circuit = design.Engineering.Circuit with
                    { Symbols = design.Engineering.Circuit.Symbols.Select(s => design.SymbolBindings.Single(b => b.SymbolOccurrenceId == s.Id).NativeObjectId == native
                        ? s with { Placement = s.Placement! with { Locked = true } } : s).ToArray() } } };
                }
            }
            state = Rebase(state, design);
            Assert.AreEqual("locked_field_owner", Assert.ThrowsExactly<AutomationException>(() => SchematicFieldLayoutPlanner.Propose(state, [Move(id)])).Code);
        }
    }

    private static DesignRecoveryState Fixture()
    {
        var state = SchematicSynchronizationPlanTests.Fixture(); var design = state.Baseline with { Schematic = state.Baseline.Schematic.Clone() };
        foreach (var screen in design.Schematic.Instances)
        for (int i = 0; i < screen.Items.Count; i++)
        {
            if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
            symbol.ReferenceField.Name = "Reference"; symbol.ValueField.Name = "Value";
            foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField })
            {
                field.Text.Position = new() { XNm = 15_000_000, YNm = 20_000_000 };
                field.Text.Attributes.Angle = new(); field.Text.Attributes.HorizontalAlignment = HorizontalAlignment.HaCenter;
                field.Text.Attributes.VerticalAlignment = VerticalAlignment.VaCenter;
                field.Visible = true;
            }
            symbol.FieldsAutoplaced = true; screen.Items[i] = Any.Pack(symbol);
        }
        return Rebase(state, design);
    }

    private static DesignRecoveryState Rebase(DesignRecoveryState state, SchematicDesign design)
    {
        var baseline = state.BaselineElectrical!.Clone(); baseline.Hierarchy.Data = design.Schematic.Clone();
        var observed = state.ObservedElectrical!.Clone(); observed.Hierarchy.Data = design.Schematic.Clone();
        return state with { Baseline = design, BaselineElectrical = baseline, ObservedElectrical = observed,
            Observed = design.Schematic.Clone(), DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
    }
}
