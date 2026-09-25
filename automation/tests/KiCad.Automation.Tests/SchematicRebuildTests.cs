using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

// Isolated rules of SchematicRebuild (ledger p168f8654d86c5a1a): which saved states generate sheets or rebuild deleted
// files, which are refused, which stay on the general path, and the exact batch each plans. The rendered PSU/CPU journey
// (NativeXmlRebuildJourney, check xml-rebuild) proves one generation and one rebuild end to end in KiCad; these offline
// cases cover every refusal and false-positive guard, which a journey cannot reach without destroying its own project.
// No existing test covers these entry points: they were an unavailable seam until this lane delivered them.
[TestClass]
public sealed class SchematicRebuildTests
{
    private static readonly Guid Root = Guid.Parse("0b6f4c33-9a71-4d0e-8f1c-2a5b6c7d8e9f");

    private static DesignRecoveryState State(SchematicDesign baseline, SchematicDesign desired, SchematicHierarchyData? observed = null,
        string baselineEpoch = "e1", string observedEpoch = "e1")
    {
        var native = observed ?? baseline.Schematic.Clone();
        var before = new SchematicElectricalState { Hierarchy = new() { Data = baseline.Schematic.Clone(), Revision = new() { Epoch = baselineEpoch, Sequence = 3 } } };
        var now = new SchematicElectricalState { Hierarchy = new() { Data = native.Clone(), Revision = new() { Epoch = observedEpoch, Sequence = 4 } } };
        return new(Guid.NewGuid(), Guid.NewGuid(), new(observedEpoch, 4), false, baseline,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, [])), native, [], BaselineElectrical: before, ObservedElectrical: now);
    }

    private static SchematicDesign RootOnly() => PsuCpuFixture.Baseline(PsuCpuFixture.SeedHierarchy(Root, PsuCpuSeed.RootOnly), PsuCpuSeed.RootOnly, Root, CancellationToken.None);

    private static SchematicDesign Placed()
    {
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        return SchematicNativeCreationProjection.Project(sheets, components, []).Candidate;
    }

    // What KiCad shows after the schematic files were deleted and it created the project's root anew: one empty,
    // never-loaded root with a screen identity of its own.
    private static SchematicHierarchyData NewEmptyRoot(SchematicDesign design)
    {
        var root = design.Schematic.Instances.Single(s => s.Metadata.Document.Equals(design.Schematic.Document)).Clone();
        root.Items.Clear(); root.CachedSymbols.Clear();
        root.Metadata.ScreenId = new KIID { Value = Guid.NewGuid().ToString("D") };
        root.Metadata.LoadedNativeFormatVersion = 0;
        var data = new SchematicHierarchyData { Document = design.Schematic.Document.Clone() };
        data.Instances.Add(root);
        return data;
    }

    [TestMethod]
    public void XmlThatAddsSheetsGeneratesThemWithIdentitiesDerivedFromTheModel()
    {
        var baseline = RootOnly();
        var desired = baseline with { Engineering = PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly) };
        var state = State(baseline, desired);
        var shape = SchematicRebuild.Classify(state, desired);
        Assert.AreEqual(SchematicRebuildKind.Admitted, shape.Kind, shape.ErrorMessage);
        Guid[] sheets = [PsuCpuIds.Id(0x05, 2), PsuCpuIds.Id(0x05, 3), PsuCpuIds.Id(0x05, 4)];
        CollectionAssert.AreEqual(sheets, shape.SheetInstanceIds.ToArray(), "Parents come before their children, in the XML's order.");

        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsTrue(plan.NativeRebuildRequired);
        Assert.IsFalse(plan.NativeConnectionRealizationRequired);
        CollectionAssert.AreEqual(sheets, plan.Rebuild!.SheetInstanceIds.ToArray());
        var created = plan.NativeOperations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true).Select(o => (o.TargetDocument, Sheet: o.Create.Unpack<SheetSymbol>())).ToArray();
        Assert.HasCount(3, created);
        string[] names = ["PSU", "CPU", "CPU_POWER"], files = ["psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch"], pages = ["2", "3", "4"];
        var paths = new Dictionary<Guid, IReadOnlyList<Guid>> { [PsuCpuIds.Id(0x05, 1)] = [Root] };
        foreach (var (id, index) in sheets.Select((id, i) => (id, i)))
        {
            var identity = SchematicRebuild.GeneratedSheet(id);
            var (target, sheet) = created.Single(c => c.Sheet.Id.Value == identity.SheetSymbolId.ToString("D"));
            Assert.AreEqual(identity.ScreenId.ToString("D"), sheet.ChildScreenId.Value);
            Assert.AreEqual(names[index], sheet.NameField.Text.Text_);
            Assert.AreEqual(files[index], sheet.FilenameField.Text.Text_);
            Assert.AreEqual(pages[index], sheet.PageNumber);
            Guid parent = index == 2 ? sheets[1] : PsuCpuIds.Id(0x05, 1);
            CollectionAssert.AreEqual(paths[parent].Select(p => p.ToString("D")).ToArray(), target.SheetPath.Path.Select(p => p.Value).ToArray(),
                names[index] + " is created on its parent sheet.");
            paths[id] = [.. paths[parent], identity.SheetSymbolId];
            var binding = plan.Candidate!.SheetBindings.Single(b => b.SheetInstanceId == id);
            CollectionAssert.AreEqual(paths[id].ToArray(), binding.NativePath.ToArray());
        }
        // Each new screen gets its page and title block in the same batch, before anything else on it.
        Assert.AreEqual(3, plan.NativeOperations.Count(o => o.SetPageSettings is not null));
        Assert.AreEqual(3, plan.NativeOperations.Count(o => o.SetTitleBlock is not null));
        Assert.IsEmpty(SchematicDesignBindings.Inspect(plan.Candidate!, []).Issues);
        // A second planner computes the same batch and design: the preview is what apply sends.
        var again = SchematicSynchronizationPlanner.Plan(state);
        Assert.AreEqual(plan.CandidateXml, again.CandidateXml);
        CollectionAssert.AreEqual(plan.NativeOperations.Select(o => o.ToString()).ToArray(), again.NativeOperations.Select(o => o.ToString()).ToArray());
    }

    [TestMethod]
    public void SheetGenerationRefusesOrLeavesEveryOtherShape()
    {
        var baseline = RootOnly();
        // New sheets that bring their components along: the components need coordinates on sheets that exist first.
        var components = baseline with { Engineering = PsuCpuFixture.Engineering(PsuCpuStage.Components) };
        var refused = SchematicRebuild.Classify(State(baseline, components), components);
        Assert.AreEqual(SchematicRebuildKind.Rejected, refused.Kind);
        Assert.AreEqual("rebuild_sheet_generation_requires_empty_sheets", refused.ErrorCode);
        Assert.AreEqual("rebuild_sheet_generation_requires_empty_sheets", SchematicSynchronizationPlanner.Plan(State(baseline, components)).ErrorCode);

        var sheets = baseline with { Engineering = PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly) };
        // Nothing new in the XML.
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, baseline), baseline).Kind);
        // KiCad changed since the last synchronization: the general path reconciles that first.
        var edited = baseline.Schematic.Clone();
        edited.Instances[0].Metadata.TitleBlock = new TitleBlockInfo { Title = "Edited in KiCad" };
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, sheets, edited), sheets).Kind);
        // The XML also edits the native snapshot.
        var snapshot = sheets with { Schematic = edited };
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, snapshot), snapshot).Kind);
        // A new sheet whose definition a second new sheet also shows.
        var circuit = sheets.Engineering.Circuit;
        var shared = sheets with { Engineering = sheets.Engineering with { Circuit = circuit with { SheetInstances =
            [.. circuit.SheetInstances.Select(s => s.Id == PsuCpuIds.Id(0x05, 3) ? s with { DefinitionId = PsuCpuIds.Id(0x04, 2) } : s)],
            Sheets = [.. circuit.Sheets.Where(s => s.Id != PsuCpuIds.Id(0x04, 3))] } } };
        var sharedShape = SchematicRebuild.Classify(State(baseline, shared), shared);
        Assert.AreEqual(SchematicRebuildKind.Rejected, sharedShape.Kind);
        Assert.AreEqual("rebuild_shared_sheet_unsupported", sharedShape.ErrorCode);
        // Pending native work is never classified.
        var pending = State(baseline, sheets) with { PendingMutation = new ApplySchematicItemBatch() };
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(pending, sheets).Kind);
    }

    [TestMethod]
    public void DeletedFilesRebuildEveryObjectWithItsIdentityAfterTheRootAdoptsItsOwn()
    {
        var baseline = Placed();
        var empty = NewEmptyRoot(baseline);
        var state = State(baseline, baseline, empty, baselineEpoch: "loaded", observedEpoch: "created");
        var shape = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, shape.Kind, shape.ErrorMessage);
        CollectionAssert.AreEquivalent(baseline.SheetBindings.Select(b => b.SheetInstanceId).ToArray(), shape.SheetInstanceIds.ToArray());

        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsTrue(plan.NativeRebuildRequired);
        var rootScreen = baseline.Schematic.Instances.Single(s => s.Metadata.Document.Equals(baseline.Schematic.Document)).Metadata.ScreenId;
        Assert.AreEqual(rootScreen, plan.NativeOperations[0].RebuildScreenIdentity, "The new root first adopts its file's identity.");
        Assert.AreEqual(1, plan.NativeOperations.Count(o => o.RebuildScreenIdentity is not null));
        var placed = baseline.Schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(placed, plan.NativeOperations.Where(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true)
            .Select(o => o.Create.Unpack<SchematicSymbolInstance>().Id.Value).Order(StringComparer.Ordinal).ToArray(), "Every symbol keeps its identity.");
        Assert.AreEqual(3, plan.NativeOperations.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        // The planned design is the saved one: same engineering and bindings, same objects on every screen.
        var candidate = plan.Candidate!;
        Assert.AreEqual(SchematicDesignXml.Write(baseline with { Schematic = candidate.Schematic }, []), SchematicDesignXml.Write(candidate, []));
        foreach (var screen in baseline.Schematic.Instances)
        {
            var planned = candidate.Schematic.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document));
            Assert.AreEqual(screen.Metadata.ScreenId, planned.Metadata.ScreenId);
            CollectionAssert.AreEqual(screen.Items.ToArray(), planned.Items.ToArray());
            CollectionAssert.AreEqual(screen.CachedSymbols.ToArray(), planned.CachedSymbols.ToArray());
        }

        // Resolve adopts KiCad's rebuilt snapshot only when it is the planned one.
        var batch = new ApplySchematicItemBatch { Description = SchematicRebuild.BatchDescription };
        batch.Operations.Add(plan.NativeOperations.Select(o => o.Clone()));
        var receipt = new CheckedSchematicBatchReceipt { Status = CheckedSchematicBatchStatus.CsbsCompleted };
        var native = new SchematicElectricalState { Hierarchy = new() { Data = candidate.Schematic.Clone() } };
        var resolved = SchematicRebuild.Resolve(candidate, state, native, batch, receipt);
        Assert.AreEqual(SchematicDesignXml.Write(candidate, []), SchematicDesignXml.Write(resolved, []));
        var drifted = candidate.Schematic.Clone();
        drifted.Instances.Last().Items.RemoveAt(0);
        Assert.AreEqual("rebuild_native_mismatch", Assert.ThrowsExactly<AutomationException>(() => SchematicRebuild.Resolve(candidate, state,
            new SchematicElectricalState { Hierarchy = new() { Data = drifted } }, batch, receipt)).Code);
    }

    [TestMethod]
    public void RebuildRefusesUnsettledXmlAndUnrepresentedStateAndIgnoresEditsInsideKiCad()
    {
        var baseline = Placed();
        var empty = NewEmptyRoot(baseline);
        // The XML changed after the files were lost: rebuild the synchronized design first.
        var circuit = baseline.Engineering.Circuit;
        var renamed = baseline with { Engineering = baseline.Engineering with { Circuit = circuit with
            { Components = [.. circuit.Components.Select(c => c.Reference == "R1" ? c with { Reference = "R9" } : c)] } } };
        var unsettled = SchematicSynchronizationPlanner.Plan(State(baseline, renamed, empty, "loaded", "created"));
        Assert.AreEqual("rebuild_requires_settled_xml", unsettled.ErrorCode);
        Assert.IsEmpty(unsettled.NativeOperations);
        // Net chains and shared screens are not rebuilt yet; the untyped project settings stay in the kept project file.
        foreach (string missing in new[] { "net_chains", "shared_screen_root_ownership", "library_cache" })
        {
            var marked = baseline with { Schematic = baseline.Schematic.Clone() };
            foreach (var screen in marked.Schematic.Instances) screen.Metadata.UnrepresentedState.Add(missing);
            var refused = SchematicSynchronizationPlanner.Plan(State(marked, marked, empty, "loaded", "created"));
            Assert.AreEqual("rebuild_state_unrepresented", refused.ErrorCode, missing);
            StringAssert.Contains(refused.ErrorMessage, missing);
        }
        var retained = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in retained.Schematic.Instances) screen.Metadata.UnrepresentedState.Add(SchematicRebuild.RetainedProjectSettings);
        var emptyRetained = NewEmptyRoot(retained);
        Assert.AreEqual(SchematicRebuildKind.Admitted, SchematicRebuild.Classify(State(retained, retained, emptyRetained, "loaded", "created"), retained).Kind);
        // Everything deleted inside KiCad keeps its document session: that is a native edit for the general path.
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, baseline, empty, "same", "same"), baseline).Kind);
        // A root KiCad loaded from a file is not a new root.
        var loaded = empty.Clone(); loaded.Instances[0].Metadata.LoadedNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion;
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, baseline, loaded, "loaded", "created"), baseline).Kind);
    }

    [TestMethod]
    public void OnlyARebuildLayoutIsRecognizedAsOne()
    {
        var baseline = RootOnly();
        var state = State(baseline, baseline);
        var batch = new ApplySchematicItemBatch { Description = SchematicRebuild.BatchDescription };
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SheetSymbol()) });
        DesignLayoutIntent Layout(string? lane) => DesignLayoutIntent.Create("/tmp/design.xml", [], [], Guid.NewGuid(), "token", lane);
        Assert.IsTrue(SchematicRebuild.IsRebuild(state with { PendingMutation = batch, PendingLayout = Layout(DesignLayoutIntent.RebuildLane) }));
        Assert.IsFalse(SchematicRebuild.IsRebuild(state with { PendingMutation = batch, PendingLayout = Layout(DesignLayoutIntent.ConnectionRealizationLane) }));
        Assert.IsFalse(SchematicRebuild.IsRebuild(state with { PendingMutation = batch, PendingLayout = Layout(null) }));
        var asserted = batch.Clone(); asserted.Operations.Add(new SchematicItemOperation { AssertConnectivity = new() { Version = 1 } });
        Assert.IsFalse(SchematicRebuild.IsRebuild(state with { PendingMutation = asserted, PendingLayout = Layout(DesignLayoutIntent.RebuildLane) }));
        var other = batch.Clone(); other.Description = "Apply XML synchronization candidate";
        Assert.IsFalse(SchematicRebuild.IsRebuild(state with { PendingMutation = other, PendingLayout = Layout(DesignLayoutIntent.RebuildLane) }));
        Assert.AreEqual("cpu_power", SchematicRebuild.GeneratedFileStem("CPU_POWER"));
        Assert.AreEqual("dc-dc_stage_2", SchematicRebuild.GeneratedFileStem(" DC-DC stage/2 "));
    }
}
