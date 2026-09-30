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

    internal static DesignRecoveryState State(SchematicDesign baseline, SchematicDesign desired, SchematicHierarchyData? observed = null,
        string baselineEpoch = "e1", string observedEpoch = "e1")
    {
        var native = observed ?? baseline.Schematic.Clone();
        var before = new SchematicElectricalState { Hierarchy = new() { Data = baseline.Schematic.Clone(), Revision = new() { Epoch = baselineEpoch, Sequence = 3 } } };
        var now = new SchematicElectricalState { Hierarchy = new() { Data = native.Clone(), Revision = new() { Epoch = observedEpoch, Sequence = 4 } } };
        return new(Guid.NewGuid(), Guid.NewGuid(), new(observedEpoch, 4), false, baseline,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, [])), native, [], BaselineElectrical: before, ObservedElectrical: now);
    }

    private static SchematicDesign RootOnly() => PsuCpuFixture.Baseline(PsuCpuFixture.SeedHierarchy(Root, PsuCpuSeed.RootOnly), PsuCpuSeed.RootOnly, Root, CancellationToken.None);

    internal static SchematicDesign Placed()
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
        root.Metadata.NetChains.Clear();
        root.Metadata.ScreenId = new KIID { Value = Guid.NewGuid().ToString("D") };
        root.Metadata.LoadedNativeFormatVersion = 0;
        var data = new SchematicHierarchyData { Document = design.Schematic.Document.Clone() };
        data.Instances.Add(root);
        return data;
    }

    internal static SchematicDesign WithRepeatedSheet(SchematicDesign source)
    {
        var result = source with { Schematic = source.Schematic.Clone() };
        // PSU owns all its placed components locally. CPU_POWER contains a unit owned
        // by another sheet and cannot be duplicated by inventing component ownership.
        var binding = result.SheetBindings.Single(b => b.SheetInstanceId == PsuCpuIds.Id(0x05, 2));
        var child = result.Schematic.Instances.Single(s =>
            s.Metadata.Document.SheetPath.Path.Select(p => Guid.Parse(p.Value)).SequenceEqual(binding.NativePath));
        var sourceInstance = result.Engineering.Circuit.SheetInstances.Single(s => s.Id == binding.SheetInstanceId);
        Guid newInstanceId = Guid.NewGuid();
        var sourceComponents = result.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == sourceInstance.Id).ToArray();
        var newComponents = sourceComponents.Select((c, i) => c with
            { Id = Guid.NewGuid(), SheetInstanceId = newInstanceId, Reference = "REPEATED" + (i + 1) }).ToArray();
        var componentMap = sourceComponents.Zip(newComponents).ToDictionary(p => p.First.Id, p => p.Second);
        var references = sourceComponents.Zip(newComponents).ToDictionary(p => p.First.Reference, p => p.Second.Reference);
        var sourceOccurrences = result.Engineering.Circuit.Symbols.Where(s => componentMap.ContainsKey(s.ComponentId)).ToArray();
        var newOccurrences = sourceOccurrences.Select(s => s with { Id = Guid.NewGuid(), ComponentId = componentMap[s.ComponentId].Id,
            SheetInstanceId = s.SheetInstanceId is null ? null : newInstanceId }).ToArray();
        var parentPath = new SheetPath();
        parentPath.Path.Add(child.Metadata.Document.SheetPath.Path.Take(child.Metadata.Document.SheetPath.Path.Count - 1));
        var parent = result.Schematic.Instances.Single(s => s.Metadata.Document.SheetPath.Equals(parentPath));
        var original = parent.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
            .Single(s => s.ChildScreenId.Equals(child.Metadata.ScreenId));
        var repeatedId = Guid.Parse("0b6f4c33-9a71-4d0e-8f1c-2a5b6c7d8ea1");
        var repeated = original.Clone();
        repeated.Id.Value = repeatedId.ToString("D");
        repeated.NameField.Text.Text_ += " repeat";
        repeated.PageNumber = "99";
        repeated.Position ??= new Vector2();
        repeated.Position.XNm += 25_400_000;
        repeated.InstanceRecords ??= new SheetPlacementRecords();
        var placement = repeated.InstanceRecords.Records.SingleOrDefault(r => r.Path.SequenceEqual(parentPath.Path));
        if (placement is null)
        {
            placement = new SheetPlacementRecord { PageNumber = repeated.PageNumber, Variants = repeated.Variants?.Clone() ?? new SheetVariants() };
            placement.Path.Add(parentPath.Path.Select(id => id.Clone()));
            repeated.InstanceRecords.Records.Add(placement);
        }
        else
            placement.PageNumber = repeated.PageNumber;
        parent.Items.Add(Any.Pack(repeated));

        var repeatedPath = child.Metadata.Document.SheetPath.Clone();
        repeatedPath.Path[^1] = new KIID { Value = repeatedId.ToString("D") };
        // A shared physical screen carries every placement record on each of its
        // instances. Add the new occurrence to the source objects before cloning
        // the repeated screen so physical-operation comparison remains identical.
        for (int index = 0; index < child.Items.Count; ++index)
        {
            var packed = child.Items[index];
            if (packed.Is(SchematicSymbolInstance.Descriptor))
            {
                var symbol = packed.Unpack<SchematicSymbolInstance>();
                if (symbol.InstanceRecords is { } records && records.Records.SingleOrDefault(r => r.Path.SequenceEqual(child.Metadata.Document.SheetPath.Path)) is { } local
                    && !records.Records.Any(r => r.Path.SequenceEqual(repeatedPath.Path)))
                {
                    var copy = local.Clone();
                    copy.Path.Clear();
                    copy.Path.Add(repeatedPath.Path.Select(id => id.Clone()));
                    copy.Reference = references[local.Reference];
                    records.Records.Add(copy);
                }
                child.Items[index] = Any.Pack(symbol);
            }
            else if (packed.Is(SheetSymbol.Descriptor))
            {
                var sheet = packed.Unpack<SheetSymbol>();
                if (sheet.InstanceRecords is { } records && records.Records.SingleOrDefault(r => r.Path.SequenceEqual(child.Metadata.Document.SheetPath.Path)) is { } local
                    && !records.Records.Any(r => r.Path.SequenceEqual(repeatedPath.Path)))
                {
                    var copy = local.Clone();
                    copy.Path.Clear();
                    copy.Path.Add(repeatedPath.Path.Select(id => id.Clone()));
                    records.Records.Add(copy);
                }
                child.Items[index] = Any.Pack(sheet);
            }
        }
        var repeatedChild = child.Clone();
        repeatedChild.Metadata.Document.SheetPath = repeatedPath.Clone();
        for (int i = 0; i < repeatedChild.Items.Count; ++i)
        {
            if (repeatedChild.Items[i].Is(SchematicSymbolInstance.Descriptor))
            {
                var symbol = repeatedChild.Items[i].Unpack<SchematicSymbolInstance>();
                symbol.Path = repeatedPath.Clone();
                symbol.ReferenceField.Text.Text_ = references[symbol.ReferenceField.Text.Text_];
                repeatedChild.Items[i] = Any.Pack(symbol);
            }
            else if (repeatedChild.Items[i].Is(SheetSymbol.Descriptor))
            {
                var sheet = repeatedChild.Items[i].Unpack<SheetSymbol>();
                sheet.Path = repeatedPath.Clone();
                repeatedChild.Items[i] = Any.Pack(sheet);
            }
        }
        result.Schematic.Instances.Add(repeatedChild);
        return result with
        {
            Engineering = result.Engineering with { Circuit = result.Engineering.Circuit with
            {
                SheetInstances = [.. result.Engineering.Circuit.SheetInstances, sourceInstance with { Id = newInstanceId }],
                Components = [.. result.Engineering.Circuit.Components, .. newComponents],
                Symbols = [.. result.Engineering.Circuit.Symbols, .. newOccurrences]
            } },
            SheetBindings = [.. result.SheetBindings, new(newInstanceId, repeatedPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())],
            SymbolBindings = [.. result.SymbolBindings, .. sourceOccurrences.Zip(newOccurrences).Select(p =>
                new SchematicSymbolBinding(p.Second.Id, result.SymbolBindings.Single(b => b.SymbolOccurrenceId == p.First.Id).NativeObjectId))]
        };
    }

    [TestMethod]
    public async Task XmlThatAddsSheetsGeneratesThemWithIdentitiesDerivedFromTheModel()
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

        // The same native sheets, when inserted in KiCad first, pass the complete
        // reconciliation pipeline with their newly adopted sheet bindings.
        string directory = Directory.CreateTempSubdirectory("kicad-native-sheet-adoption-").FullName;
        try
        {
            var store = new DesignRecoveryStore(Path.Combine(directory, "recovery.json"));
            string epoch = Guid.NewGuid().ToString("D");
            var saved = store.Save(State(baseline, baseline, plan.Candidate!.Schematic, epoch, epoch), null);
            var nativeAdded = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(store, saved);
            Assert.IsTrue(nativeAdded.CanPrepare, nativeAdded.ErrorCode + ": " + nativeAdded.ErrorMessage);
            Assert.IsEmpty(nativeAdded.NativeOperations);
            Assert.HasCount(4, nativeAdded.Candidate!.SheetBindings);
            Assert.HasCount(3, nativeAdded.Electrical!.AddedSheetInstances!);
            var loaded = nativeAdded.Candidate with { Schematic = nativeAdded.Candidate.Schematic.Clone() };
            foreach (var screen in loaded.Schematic.Instances) screen.Metadata.LoadedNativeFormatVersion = screen.Metadata.WriterNativeFormatVersion;
            Assert.IsFalse(SchematicSynchronizationExecutor.Equivalent(baseline, loaded, saved.State, CancellationToken.None),
                "A newly loaded native sheet makes designs unequal; equality must not try to recreate it.");
            Assert.IsFalse(SchematicSynchronizationExecutor.Equivalent(loaded, baseline, saved.State, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }

        var complete = plan.Candidate!;
        Guid movedId = sheets[2], parentId = PsuCpuIds.Id(0x05, 1);
        var movedModel = complete with { Engineering = complete.Engineering with { Circuit = complete.Engineering.Circuit with
        { SheetInstances = complete.Engineering.Circuit.SheetInstances.Select(s => s.Id == movedId ? s with { ParentId = parentId } : s).ToArray() } } };
        var movedPlan = SchematicSynchronizationPlanner.Plan(State(complete, movedModel));
        Assert.IsTrue(movedPlan.CanPrepare, movedPlan.ErrorCode + ": " + movedPlan.ErrorMessage);
        Assert.AreEqual(parentId, movedPlan.Candidate!.Engineering.Circuit.SheetInstances.Single(s => s.Id == movedId).ParentId);
        Assert.AreEqual(complete.SheetBindings.Single(b => b.SheetInstanceId == movedId).NativePath[^1],
            movedPlan.Candidate.SheetBindings.Single(b => b.SheetInstanceId == movedId).NativePath[^1]);
        Assert.HasCount(1, movedPlan.NativeOperations.Where(o => o.Remove is not null));
        Assert.HasCount(1, movedPlan.NativeOperations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        var removedIds = new[] { sheets[1], sheets[2] }.ToHashSet();
        var removedDefinitions = complete.Engineering.Circuit.SheetInstances.Where(s => removedIds.Contains(s.Id)).Select(s => s.DefinitionId).ToHashSet();
        var removedModel = complete with { Engineering = complete.Engineering with { Circuit = complete.Engineering.Circuit with
        { SheetInstances = complete.Engineering.Circuit.SheetInstances.Where(s => !removedIds.Contains(s.Id)).ToArray(),
            Sheets = complete.Engineering.Circuit.Sheets.Where(s => !removedDefinitions.Contains(s.Id)).ToArray() } },
            SheetBindings = complete.SheetBindings.Where(s => !removedIds.Contains(s.SheetInstanceId)).ToArray() };
        var removedPlan = SchematicSynchronizationPlanner.Plan(State(complete, removedModel));
        Assert.IsTrue(removedPlan.CanPrepare, removedPlan.ErrorCode + ": " + removedPlan.ErrorMessage);
        CollectionAssert.AreEquivalent(removedIds.ToArray(), removedPlan.Electrical!.RemovedSheetInstances!.ToArray());
        Assert.HasCount(1, removedPlan.NativeOperations.Where(o => o.Remove is not null), "Unlink the parent once; do not delete detached child contents.");
        var concurrent = complete.Schematic.Clone();
        concurrent.Instances[0].Metadata.TitleBlock ??= new();
        concurrent.Instances[0].Metadata.TitleBlock.Title = "Concurrent native change";
        var conflict = SchematicSynchronizationPlanner.Plan(State(complete, movedModel, concurrent));
        Assert.IsFalse(conflict.CanPrepare); Assert.IsEmpty(conflict.NativeOperations);
        Assert.AreEqual("ownership_change_with_xml_edits", conflict.ErrorCode);
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
        // KiCad names the untyped project settings, shared-screen root ownership and net chains on every snapshot. A design
        // with no sheet file shown twice and no net chain loses nothing to the latter two, and the project settings stay in
        // the kept project file: it is rebuilt.
        var covered = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in covered.Schematic.Instances)
            screen.Metadata.UnrepresentedState.Add(new[] { SchematicRebuild.RetainedProjectSettings, "shared_screen_root_ownership", "net_chains" });
        Assert.AreEqual(SchematicRebuildKind.Admitted, SchematicRebuild.Classify(State(covered, covered, NewEmptyRoot(covered), "loaded", "created"), covered).Kind);
        // Net chains are typed schematic metadata and are rebuilt with the rest of the lost files.
        var chained = covered with { Schematic = covered.Schematic.Clone() };
        foreach (var screen in chained.Schematic.Instances) screen.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "DATA_PATH", Committed = true,
            From = new() { Reference = "U1", Pin = "1" }, To = new() { Reference = "U2", Pin = "1" }, MemberNets = { "/DATA" } });
        var withChains = SchematicRebuild.Classify(State(chained, chained, NewEmptyRoot(chained), "loaded", "created"), chained);
        Assert.AreEqual(SchematicRebuildKind.Admitted, withChains.Kind, withChains.ErrorMessage);
        var chainedPlan = SchematicSynchronizationPlanner.Plan(State(chained, chained, NewEmptyRoot(chained), "loaded", "created"));
        Assert.IsTrue(chainedPlan.CanPrepare, chainedPlan.ErrorCode + ": " + chainedPlan.ErrorMessage);
        Assert.IsTrue(chainedPlan.NativeOperations.Any(o => o.ReplaceNetChains is not null), "The rebuild carries the typed net-chain state into the native batch.");
        Assert.IsTrue(chainedPlan.NativeOperations.Where(o => o.ReplaceNetChains is not null)
            .SelectMany(o => o.ReplaceNetChains.Definitions).All(c => !c.Committed), "Rebuild recreates declarations without writing the historical computed bit.");
        Assert.IsTrue(chainedPlan.Candidate!.Schematic.Instances.All(s => s.Metadata.NetChains.Single().Committed),
            "The expected rebuilt snapshot still requires the original computed result.");
        {
            var marked = covered with { Schematic = covered.Schematic.Clone() };
            foreach (var screen in marked.Schematic.Instances) screen.Metadata.UnrepresentedState.Add("future_state_group");
            var refused = SchematicSynchronizationPlanner.Plan(State(marked, marked, NewEmptyRoot(covered), "loaded", "created"));
            Assert.AreEqual("rebuild_state_unrepresented", refused.ErrorCode);
            StringAssert.Contains(refused.ErrorMessage, "future_state_group");
            Assert.IsEmpty(refused.NativeOperations);
        }
        // Ledger p91fda8ca22a68141: preview 23 and earlier named library_cache on every snapshot although they held each
        // screen's cache exactly. Their record is rebuilt when every placed symbol's definition is in its screen's cache
        // (precision), with the upgraded KiCad's own coverage list; one missing definition is a loss (must-catch).
        Assert.IsTrue(covered.Schematic.Instances.Any(s => s.CachedSymbols.Count != 0), "The placed design keeps its library caches.");
        var preview23 = covered with { Schematic = covered.Schematic.Clone() };
        foreach (var screen in preview23.Schematic.Instances) screen.Metadata.UnrepresentedState.Insert(2, "library_cache");
        Assert.IsFalse(SchematicRebuild.Lost("library_cache", preview23.Schematic));
        var upgradedRoot = NewEmptyRoot(covered);
        var rebuilt = SchematicSynchronizationPlanner.Plan(State(preview23, preview23, upgradedRoot, "loaded", "created"));
        Assert.IsTrue(rebuilt.CanPrepare, rebuilt.ErrorCode + ": " + rebuilt.ErrorMessage);
        Assert.IsTrue(rebuilt.NativeRebuildRequired);
        foreach (var screen in rebuilt.Candidate!.Schematic.Instances)
            CollectionAssert.AreEqual(upgradedRoot.Instances[0].Metadata.UnrepresentedState.ToArray(), screen.Metadata.UnrepresentedState.ToArray(),
                "The rebuilt design carries the upgraded KiCad's coverage list.");
        var incomplete = preview23 with { Schematic = preview23.Schematic.Clone() };
        incomplete.Schematic.Instances.First(s => s.CachedSymbols.Count != 0).CachedSymbols.RemoveAt(0);
        Assert.IsTrue(SchematicRebuild.Lost("library_cache", incomplete.Schematic));
        var lost = SchematicSynchronizationPlanner.Plan(State(incomplete, incomplete, NewEmptyRoot(covered), "loaded", "created"));
        Assert.AreEqual("rebuild_state_unrepresented", lost.ErrorCode, lost.ErrorMessage);
        StringAssert.Contains(lost.ErrorMessage, "library_cache");
        Assert.IsEmpty(lost.NativeOperations);
        // Repeated sheet instances share one physical screen and are rebuilt with their exact native identities.
        Assert.IsFalse(SchematicRebuild.Lost("shared_screen_root_ownership", covered.Schematic));
        var repeated = covered.Schematic.Clone();
        var child = repeated.Instances.First(s => !s.Metadata.Document.Equals(repeated.Document)).Clone();
        child.Metadata.Document.SheetPath.Path[^1] = new KIID { Value = Guid.NewGuid().ToString("D") };
        repeated.Instances.Add(child);
        Assert.IsFalse(SchematicRebuild.Lost("shared_screen_root_ownership", repeated));
        Assert.IsFalse(SchematicRebuild.Lost("net_chains", chained.Schematic));
        // A second root still cannot be represented by one project rebuild.
        var twoRoots = covered.Schematic.Clone();
        var second = twoRoots.Instances.Single(s => s.Metadata.Document.Equals(twoRoots.Document)).Clone();
        second.Metadata.Document.SheetPath.Path[0] = new KIID { Value = Guid.NewGuid().ToString("D") };
        second.Metadata.ScreenId = new KIID { Value = Guid.NewGuid().ToString("D") };
        twoRoots.Instances.Add(second);
        Assert.IsTrue(SchematicRebuild.Lost("shared_screen_root_ownership", twoRoots));
        Assert.IsFalse(SchematicRebuild.Lost(SchematicRebuild.RetainedProjectSettings, twoRoots));
        // Everything deleted inside KiCad keeps its document session: that is a native edit for the general path.
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, baseline, empty, "same", "same"), baseline).Kind);
        // A root KiCad loaded from a file is not a new root.
        var loaded = empty.Clone(); loaded.Instances[0].Metadata.LoadedNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion;
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, baseline, loaded, "loaded", "created"), baseline).Kind);
    }

    // A design whose sheets sheet generation made (so every sheet symbol carries a generated identity), with one sheet
    // pin drawn on the PSU sheet symbol, as a person connecting PSU to the root would.
    private static (SchematicDesign Design, string PsuSheetId) GeneratedWithSheetPin()
    {
        var root = RootOnly();
        var sheets = root with { Engineering = PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly) };
        var generation = SchematicSynchronizationPlanner.Plan(State(root, sheets));
        Assert.IsTrue(generation.CanPrepare, generation.ErrorCode + ": " + generation.ErrorMessage);
        var design = generation.Candidate! with { Schematic = generation.Candidate!.Schematic.Clone() };
        var rootScreen = design.Schematic.Instances.Single(s => s.Metadata.Document.Equals(design.Schematic.Document));
        string psuId = SchematicRebuild.GeneratedSheet(PsuCpuIds.Id(0x05, 2)).SheetSymbolId.ToString("D");
        int index = rootScreen.Items.ToList().FindIndex(i => i.Is(SheetSymbol.Descriptor) && i.Unpack<SheetSymbol>().Id.Value == psuId);
        var psu = rootScreen.Items[index].Unpack<SheetSymbol>();
        psu.Pins.Add(new SheetPin
        {
            Id = new KIID { Value = Guid.NewGuid().ToString("D") },
            Position = new Vector2 { XNm = psu.Position.XNm + psu.Size.XNm, YNm = psu.Position.YNm + 5_080_000 },
            Text = new Text { Text_ = "TELEM_MCU_TO_CPU", Attributes = new TextAttributes { Size = new Vector2 { XNm = 1_270_000, YNm = 1_270_000 } } },
            SpinStyle = SchematicLabelSpinStyle.SlssLeft, Shape = SchematicLabelShape.SlshPassive, Side = SheetSide.ShsRight,
            Locked = LockedState.LsUnlocked
        });
        rootScreen.Items[index] = Any.Pack(psu);
        return (design, psuId);
    }

    private static SchematicHierarchyData WithSheet(SchematicHierarchyData data, string sheetId, Action<SheetSymbol> edit)
    {
        var result = data.Clone();
        foreach (var screen in result.Instances)
            for (int i = 0; i < screen.Items.Count; ++i)
                if (screen.Items[i].Is(SheetSymbol.Descriptor) && screen.Items[i].Unpack<SheetSymbol>() is { } sheet && sheet.Id.Value == sheetId)
                {
                    edit(sheet);
                    screen.Items[i] = Any.Pack(sheet);
                }
        return result;
    }

    // Review finding (lane 2C, xml-rebuild): the relaxed check for sheet symbols that sheet generation creates also caught a
    // rebuild, whose sheet symbols all carry generated identities. A rebuilt sheet symbol with sheet pins was then refused
    // after KiCad had committed it, and any other difference on a rebuilt sheet symbol was accepted unseen.
    [TestMethod]
    public void RebuiltSheetSymbolsKeepTheirSheetPinsAndMustMatchExactly()
    {
        var (baseline, psuId) = GeneratedWithSheetPin();
        var state = State(baseline, baseline, NewEmptyRoot(baseline), baselineEpoch: "loaded", observedEpoch: "created");
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsTrue(plan.NativeRebuildRequired);
        var created = plan.NativeOperations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true).Select(o => o.Create.Unpack<SheetSymbol>())
            .Single(s => s.Id.Value == psuId);
        Assert.HasCount(1, created.Pins, "The rebuilt sheet symbol is created with its sheet pin and that pin's identity.");
        var batch = new ApplySchematicItemBatch { Description = SchematicRebuild.BatchDescription };
        batch.Operations.Add(plan.NativeOperations.Select(o => o.Clone()));
        var receipt = new CheckedSchematicBatchReceipt { Status = CheckedSchematicBatchStatus.CsbsCompleted };
        var candidate = plan.Candidate!;
        SchematicDesign Resolve(SchematicHierarchyData native) =>
            SchematicRebuild.Resolve(candidate, state, new SchematicElectricalState { Hierarchy = new() { Data = native } }, batch, receipt);

        // KiCad rebuilt exactly the plan, sheet pin included: that is what is published.
        Assert.AreEqual(SchematicDesignXml.Write(candidate, []), SchematicDesignXml.Write(Resolve(candidate.Schematic.Clone()), []));
        // Must-catch: any difference on a rebuilt sheet symbol is refused, whatever the checked subset of fields says.
        foreach (var (what, edit) in new (string, Action<SheetSymbol>)[]
        {
            ("border stroke", s => s.BorderStroke = new StrokeAttributes { Width = new Distance { ValueNm = 254_000 }, Style = StrokeLineStyle.SlsDash }),
            ("fill", s => s.Fill = new GraphicFillAttributes { FillType = GraphicFillType.GftFilled }),
            ("sheet pin", s => s.Pins[0].Position.YNm += 2_540_000),
            ("missing sheet pin", s => s.Pins.Clear()),
            ("excluded from BOM", s => s.ExcludeFromBom = !s.ExcludeFromBom),
        })
            Assert.AreEqual("rebuild_native_mismatch", Assert.ThrowsExactly<AutomationException>(() =>
                Resolve(WithSheet(candidate.Schematic, psuId, edit))).Code, what);

        // Precision: sheet generation still adopts KiCad's own rendering of a sheet symbol it asked for.
        var root = RootOnly();
        var sheets = root with { Engineering = PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly) };
        var generationState = State(root, sheets);
        var generation = SchematicSynchronizationPlanner.Plan(generationState);
        var generationBatch = new ApplySchematicItemBatch { Description = SchematicRebuild.BatchDescription };
        generationBatch.Operations.Add(generation.NativeOperations.Select(o => o.Clone()));
        var rendered = WithSheet(generation.Candidate!.Schematic, psuId, s => s.BorderStroke = new StrokeAttributes
            { Width = new Distance { ValueNm = 152_400 }, Style = StrokeLineStyle.SlsSolid });
        var adopted = SchematicRebuild.Resolve(generation.Candidate!, generationState,
            new SchematicElectricalState { Hierarchy = new() { Data = rendered } }, generationBatch, receipt);
        Assert.AreEqual(rendered, adopted.Schematic, "Generation publishes KiCad's copy of the sheet symbols it created.");
        // A generated sheet symbol never arrives with sheet pins: KiCad would have made something the plan did not ask for.
        Assert.AreEqual("rebuild_native_mismatch", Assert.ThrowsExactly<AutomationException>(() => SchematicRebuild.Resolve(generation.Candidate!,
            generationState, new SchematicElectricalState { Hierarchy = new() { Data = baseline.Schematic.Clone() } }, generationBatch, receipt)).Code);
    }

    [TestMethod]
    public void DeletedFilesRebuildSharedScreensAndNetChains()
    {
        var placed = Placed();
        var repeated = WithRepeatedSheet(placed);
        var chained = repeated with { Schematic = repeated.Schematic.Clone() };
        foreach (var screen in chained.Schematic.Instances)
            screen.Metadata.NetChains.Add(new SchematicNetChainDefinition
            {
                Name = "REBUILD_CHAIN", From = new() { Reference = "U1", Pin = "1" },
                To = new() { Reference = "U2", Pin = "1" }, MemberNets = { "/REBUILD" }
            });

        var state = State(chained, chained, NewEmptyRoot(chained), "loaded", "created");
        var bindings = SchematicDesignBindings.Inspect(chained, []);
        Assert.IsTrue(bindings.IdentitiesResolved, string.Join(", ", bindings.Issues.Select(i => i.Code)));
        Assert.IsEmpty(bindings.Differences);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsTrue(plan.NativeRebuildRequired);
        Assert.IsEmpty(plan.BindingIssues);
        var operations = plan.NativeOperations;
        Assert.HasCount(1, operations.Where(o => o.ReplaceNetChains is not null), "Project-wide net chains are emitted once.");
        var shared = chained.Schematic.Instances.GroupBy(s => s.Metadata.ScreenId.Value, StringComparer.Ordinal).Single(g => g.Count() == 2);
        Assert.AreEqual(2, shared.Count());
        var physicalSymbols = shared.First().Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value).ToHashSet();
        Assert.HasCount(physicalSymbols.Count, operations.Where(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true
            && physicalSymbols.Contains(o.Create.Unpack<SchematicSymbolInstance>().Id.Value)), "Each physical shared symbol is created once.");
        Assert.IsTrue(SchematicDesignBindings.Inspect(plan.Candidate!, []).IdentitiesResolved);
        Assert.HasCount(chained.Schematic.Instances.Count, plan.Candidate!.Schematic.Instances);
        // Missing path-specific bindings must still fail the complete planner, not vanish behind Classify.
        var missing = chained with { SheetBindings = chained.SheetBindings.SkipLast(1).ToArray() };
        var refused = SchematicSynchronizationPlanner.Plan(State(missing, missing, NewEmptyRoot(missing), "loaded", "created"));
        Assert.IsFalse(refused.CanPrepare);
        Assert.IsEmpty(refused.NativeOperations);

    }

    // All captured project settings are restored from settled XML. Legacy-omitted message groups cannot
    // become guessed defaults. NativeXmlRebuildJourney proves application/save/reopen; these cases extend
    // the existing rebuild suite to cover each group's planner payload, missing coverage and journal boundary.
    private static readonly (string What, Action<SchematicMetadata> Edit)[] ProjectSettingGroups =
    [
        ("text variables", m => m.TextVariables["REVISION"] = "B"),
        ("bus aliases", m => m.BusAliases.Add(new SchematicBusAlias { Name = "REBUILD_BUS", Members = { "D0", "D1" } })),
        ("variants", m => m.VariantDescriptions["Rebuild"] = "rebuild variant"),
        ("drawing ratios", m => m.DrawingRatios!.DashLengthRatio += 1),
        ("formatting", m => m.Formatting!.DefaultTextSizeNm += 254_000),
        ("annotation", m => m.Annotation!.StartAfter++),
        ("field templates", m => m.FieldTemplates!.Fields.Add(new SchematicFieldTemplate { Name = "RebuildNote", Visible = true })),
        ("symbol comparison", m => m.SymbolComparison!.MissingFields = !m.SymbolComparison.MissingFields),
        ("BOM settings", m => m.BomSettings!.ExportFilename = "${PROJECTNAME}-rebuild.csv"),
        ("net classes", m => m.NetSettings!.DefaultClass.Priority--),
        ("used references", m => m.ReferenceInventory!.Allocated.Add("R999")),
        ("net chain classes", m => m.NetChainClasses!.Definitions.Add("rebuild-extra")),
        ("ERC settings", m => m.ErcSettings!.RuleSeverities[0].Severity = m.ErcSettings.RuleSeverities[0].Severity == RuleSeverity.RsWarning
            ? RuleSeverity.RsError : RuleSeverity.RsWarning),
        ("ngspice settings", m => m.NgspiceSettings!.ModelMode = m.NgspiceSettings.ModelMode == 2 ? 3 : 2),
    ];

    // A complete, valid formatting replacement (SchematicFormatting.Validate) with the given default text size.
    private static SchematicFormattingSettings Formatting(long textSizeNm) => new()
    {
        DefaultLineWidthNm = 152_400, DefaultTextSizeNm = textSizeNm, PinSymbolSizeNm = 635_000, ConnectionGridNm = 1_270_000,
        JunctionSizeChoice = 3, HopOverSizeChoice = 0,
        OperatingPoint = new() { VoltagePrecision = 3, VoltageRange = "~V", CurrentPrecision = 3, CurrentRange = "~A" },
        UnitReference = new() { SeparatorAscii = 0, FirstIdAscii = 'A' }
    };

    private static SchematicNgspiceSettings Ngspice() => new()
    { WorkbookFilename = "fixture.raw", FixIncludePaths = true, ModelMode = 2 };

    private static void PopulateTypedProjectSettings(SchematicDesign design)
    {
        foreach (var screen in design.Schematic.Instances)
        {
            var metadata = screen.Metadata;
            metadata.TextVariables.Clear();
            metadata.TextVariables["REVISION"] = "A";
            metadata.BusAliases.Clear();
            metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
            metadata.VariantDescriptions.Clear();
            metadata.VariantDescriptions["Assembly"] = "original";
            metadata.DrawingRatios = new SchematicDrawingRatios { DashLengthRatio = 12, GapLengthRatio = 3,
                TextOffsetRatio = 0.15, LabelSizeRatio = 0.375, OverbarHeightRatio = 1.23 };
            metadata.Formatting = Formatting(1_270_000);
            metadata.Annotation = SchematicAnnotationTests.Policy();
            metadata.FieldTemplates = new SchematicFieldTemplates();
            metadata.FieldTemplates.Fields.Add(new SchematicFieldTemplate { Name = "Supplier", Visible = true });
            metadata.SymbolComparison = new SchematicSymbolComparisonSettings { MissingFields = false, FieldTexts = false };
            metadata.BomSettings = SchematicBomSettingsTests.Settings();
            metadata.NetSettings = SchematicNetSettingsSnapshotTests.Settings();
            metadata.ReferenceInventory = new SchematicReferenceInventory { Allocated = { "R7" } };
            metadata.NetChainClasses = new SchematicNetChainClassState { Definitions = { "fastbus" } };
            metadata.ErcSettings = SchematicErcSettingsTests.Fixture();
            metadata.NgspiceSettings = Ngspice();
        }
    }

    private static SchematicItemOperation ExpectedProjectOperation(SchematicMetadata metadata, string what) => what switch
    {
        "text variables" => new() { ReplaceTextVariables = new() { Variables = { metadata.TextVariables } } },
        "bus aliases" => new() { ReplaceBusAliases = new() { Aliases = { metadata.BusAliases.Select(a => a.Clone()) } } },
        "variants" => new() { ReplaceVariantRegistry = new() { Descriptions = { metadata.VariantDescriptions } } },
        "drawing ratios" => new() { SetDrawingRatios = metadata.DrawingRatios.Clone() },
        "formatting" => new() { SetFormatting = metadata.Formatting.Clone() },
        "annotation" => new() { SetAnnotation = metadata.Annotation.Clone() },
        "field templates" => new() { SetFieldTemplates = metadata.FieldTemplates.Clone() },
        "symbol comparison" => new() { SetSymbolComparison = metadata.SymbolComparison.Clone() },
        "BOM settings" => new() { SetBomSettings = metadata.BomSettings.Clone() },
        "net classes" => new() { SetNetSettings = SchematicNetSettingsState.Declared(metadata.NetSettings) },
        "used references" => new() { SetReferenceInventory = SchematicReferenceInventoryState.Normalize(metadata.ReferenceInventory) },
        "net chain classes" => new() { ReplaceNetChainClasses = SchematicNetChainClasses.Normalize(metadata.NetChainClasses) },
        "ERC settings" => new() { SetErcSettings = SchematicErcSettingsValidation.Normalize(metadata.ErcSettings,
            metadata.ErcSettings.RuleSeverities.Select(r => r.RuleType).ToHashSet()) },
        "ngspice settings" => new() { SetNgspiceSettings = metadata.NgspiceSettings.Clone() },
        _ => throw new AssertFailedException("Unknown typed setting: " + what)
    };

    private static void AssertRebuildCandidate(SchematicDesign expected, SchematicSynchronizationPlan plan,
        SchematicHierarchyData observed, string what)
    {
        Assert.IsNotNull(plan.Candidate, what);
        var recreated = expected.Schematic.Clone();
        foreach (var screen in recreated.Instances)
        {
            screen.Metadata.LoadedNativeFormatVersion = 0;
            screen.Metadata.UnrepresentedState.Clear();
            screen.Metadata.UnrepresentedState.Add(observed.Instances[0].Metadata.UnrepresentedState);
        }
        Assert.AreEqual(recreated, plan.Candidate.Schematic, what + ": every screen retains the XML settings and objects");
        Assert.AreEqual(SchematicDesignXml.Write(expected with { Schematic = recreated }, []), plan.CandidateXml,
            what + ": the published candidate is exactly the planned XML");
    }

    [TestMethod]
    public void RebuildPlansEveryCapturedTypedProjectSetting()
    {
        var baseline = Placed();
        PopulateTypedProjectSettings(baseline);
        Assert.HasCount(14, ProjectSettingGroups, "Every typed project-setting group has a rebuild contract.");
        var recorded = baseline.Schematic.Instances.Single(s => s.Metadata.Document.Equals(baseline.Schematic.Document)).Metadata;
        var projectOperationKinds = ProjectSettingGroups.Select(g => ExpectedProjectOperation(recorded, g.What).OperationCase).ToHashSet();
        foreach (var (what, edit) in ProjectSettingGroups)
        {
            var kept = NewEmptyRoot(baseline);
            edit(kept.Instances[0].Metadata);
            var expectedOperation = ExpectedProjectOperation(recorded, what);
            Assert.AreNotEqual(expectedOperation, ExpectedProjectOperation(kept.Instances[0].Metadata, what),
                what + ": the fixture must change this setting");
            var state = State(baseline, baseline, kept, "loaded", "created");
            var shape = SchematicRebuild.Classify(state, baseline);
            Assert.AreEqual(SchematicRebuildKind.Admitted, shape.Kind, what + ": " + shape.ErrorMessage);
            var plan = SchematicSynchronizationPlanner.Plan(state);
            Assert.IsTrue(plan.CanPrepare, what + ": " + plan.ErrorCode + ": " + plan.ErrorMessage);
            Assert.IsTrue(plan.NativeRebuildRequired, what);
            var settingOperations = plan.NativeOperations.Where(o => projectOperationKinds.Contains(o.OperationCase)).ToArray();
            Assert.HasCount(1, settingOperations, what + ": exactly one project setting changes, once across the hierarchy");
            var operation = settingOperations.Single();
            Assert.IsNotNull(operation.TargetDocument, what + ": project operation target");
            expectedOperation.TargetDocument = operation.TargetDocument.Clone();
            Assert.AreEqual(expectedOperation, operation, what + ": the operation restores the complete recorded payload");
            Assert.IsTrue(SchematicRebuild.RecreatesFileState(operation, 1), what);
            AssertRebuildCandidate(baseline, plan, kept, what);
        }

        var unchanged = NewEmptyRoot(baseline);
        var unchangedPlan = SchematicSynchronizationPlanner.Plan(State(baseline, baseline, unchanged, "loaded", "created"));
        Assert.IsTrue(unchangedPlan.CanPrepare, unchangedPlan.ErrorCode + ": " + unchangedPlan.ErrorMessage);
        Assert.IsFalse(unchangedPlan.NativeOperations.Any(o => projectOperationKinds.Contains(o.OperationCase)),
            "Unchanged captured project settings do not acquire redundant edits while the sheets rebuild.");

        // The schematic file's own page is still recreated independently of project settings.
        var fresh = NewEmptyRoot(baseline);
        fresh.Instances[0].Metadata.Page = new PageSettings { PageSize = PageSize.PsA2 };
        var pagePlan = SchematicSynchronizationPlanner.Plan(State(baseline, baseline, fresh, "loaded", "created"));
        Assert.IsTrue(pagePlan.CanPrepare, pagePlan.ErrorCode + ": " + pagePlan.ErrorMessage);
        Assert.AreEqual(1, pagePlan.NativeOperations.Count(o => o.SetPageSettings is not null
            && o.TargetDocument.Equals(baseline.Schematic.Document)));
    }

    [TestMethod]
    public void RebuildAdmitsTextVariableReplacementAndJournalsIt()
    {
        var baseline = Placed();
        foreach (var screen in baseline.Schematic.Instances) screen.Metadata.TextVariables["REVISION"] = "A";
        var kept = NewEmptyRoot(baseline);
        kept.Instances[0].Metadata.TextVariables["REVISION"] = "B";
        var state = State(baseline, baseline, kept, "loaded", "created");

        var classified = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, classified.Kind, classified.ErrorMessage);

        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsTrue(plan.NativeRebuildRequired);
        var variables = plan.NativeOperations.Single(o => o.ReplaceTextVariables is not null).ReplaceTextVariables;
        Assert.AreEqual("REVISION", variables.Variables.Single().Key);
        Assert.AreEqual("A", variables.Variables.Single().Value);
        Assert.IsTrue(SchematicRebuild.RecreatesFileState(plan.NativeOperations.Single(o => o.ReplaceTextVariables is not null), 1));

        // A legacy XML record that omitted a typed setting still refuses a native value rather than inventing a default.
        var omittedRecorded = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in omittedRecorded.Schematic.Instances) screen.Metadata.Annotation = null;
        var protectedObserved = NewEmptyRoot(omittedRecorded);
        protectedObserved.Instances[0].Metadata.Annotation = SchematicAnnotationTests.Policy();
        protectedObserved.Instances[0].Metadata.TextVariables["REVISION"] = "B";
        var protectedState = State(omittedRecorded, omittedRecorded, protectedObserved, "loaded", "created");
        var protectedMixed = SchematicRebuild.Classify(protectedState, omittedRecorded);
        Assert.AreEqual("rebuild_project_settings_changed", protectedMixed.ErrorCode);
        StringAssert.Contains(protectedMixed.ErrorMessage, "annotation");

    }

    [TestMethod]
    public void RebuildAdmitsBusAliasReplacementAndJournalsIt()
    {
        var baseline = Placed();
        foreach (var screen in baseline.Schematic.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        var kept = NewEmptyRoot(baseline);
        kept.Instances[0].Metadata.BusAliases.Clear();
        var state = State(baseline, baseline, kept, "loaded", "created");

        var classified = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, classified.Kind, classified.ErrorMessage);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        var aliases = plan.NativeOperations.Single(o => o.ReplaceBusAliases is not null).ReplaceBusAliases;
        Assert.AreEqual("DATA", aliases.Aliases.Single().Name);
        Assert.IsTrue(SchematicRebuild.RecreatesFileState(plan.NativeOperations.Single(o => o.ReplaceBusAliases is not null), 1));
    }

    [TestMethod]
    public void RebuildAdmitsVariantReplacementAndJournalsIt()
    {
        var baseline = Placed();
        foreach (var screen in baseline.Schematic.Instances) screen.Metadata.VariantDescriptions["Assembly"] = "original";
        var kept = NewEmptyRoot(baseline);
        kept.Instances[0].Metadata.VariantDescriptions.Clear();
        var state = State(baseline, baseline, kept, "loaded", "created");

        var classified = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, classified.Kind, classified.ErrorMessage);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        var variants = plan.NativeOperations.Single(o => o.ReplaceVariantRegistry is not null).ReplaceVariantRegistry;
        Assert.AreEqual("original", variants.Descriptions["Assembly"]);
        Assert.IsTrue(SchematicRebuild.RecreatesFileState(plan.NativeOperations.Single(o => o.ReplaceVariantRegistry is not null), 1));
    }

    [TestMethod]
    public void RebuildAdmitsDrawingRatiosReplacementAndJournalsIt()
    {
        var baseline = Placed();
        baseline.Schematic.Instances[0].Metadata.DrawingRatios = new SchematicDrawingRatios { DashLengthRatio = 12, GapLengthRatio = 3,
            TextOffsetRatio = 0.15, LabelSizeRatio = 0.375, OverbarHeightRatio = 1.23 };
        foreach (var screen in baseline.Schematic.Instances) screen.Metadata.DrawingRatios = baseline.Schematic.Instances[0].Metadata.DrawingRatios.Clone();
        var kept = NewEmptyRoot(baseline);
        var changed = kept.Instances[0].Metadata.DrawingRatios!.Clone(); changed.DashLengthRatio += 1;
        kept.Instances[0].Metadata.DrawingRatios = changed;
        var state = State(baseline, baseline, kept, "loaded", "created");
        var classified = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, classified.Kind, classified.ErrorMessage);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsNotNull(plan.NativeOperations.Single(o => o.SetDrawingRatios is not null).SetDrawingRatios);
        Assert.IsTrue(SchematicRebuild.RecreatesFileState(plan.NativeOperations.Single(o => o.SetDrawingRatios is not null), 1));
        var omittedRecorded = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in omittedRecorded.Schematic.Instances) screen.Metadata.DrawingRatios = null;
        var omittedObserved = NewEmptyRoot(omittedRecorded);
        omittedObserved.Instances[0].Metadata.DrawingRatios = baseline.Schematic.Instances[0].Metadata.DrawingRatios.Clone();
        var omittedResult = SchematicRebuild.Classify(State(omittedRecorded, omittedRecorded, omittedObserved, "loaded", "created"), omittedRecorded);
        Assert.AreEqual("rebuild_project_settings_changed", omittedResult.ErrorCode);
        StringAssert.Contains(omittedResult.ErrorMessage, "drawing ratios");
    }

    [TestMethod]
    public void RebuildAdmitsFormattingReplacementAndJournalsIt()
    {
        var baseline = Placed();
        foreach (var screen in baseline.Schematic.Instances) screen.Metadata.Formatting = Formatting(1_270_000);
        var kept = NewEmptyRoot(baseline);
        var changed = Formatting(1_524_000);
        kept.Instances[0].Metadata.Formatting = changed;
        var state = State(baseline, baseline, kept, "loaded", "created");
        var classified = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, classified.Kind, classified.ErrorMessage);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        var operation = plan.NativeOperations.Single(o => o.SetFormatting is not null);
        Assert.AreEqual(Formatting(1_270_000), operation.SetFormatting);
        Assert.IsTrue(SchematicRebuild.RecreatesFileState(operation, 1));
        var omittedRecorded = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in omittedRecorded.Schematic.Instances) screen.Metadata.Formatting = null;
        var omittedObserved = NewEmptyRoot(omittedRecorded);
        omittedObserved.Instances[0].Metadata.Formatting = Formatting(1_270_000);
        var omittedResult = SchematicRebuild.Classify(State(omittedRecorded, omittedRecorded, omittedObserved, "loaded", "created"), omittedRecorded);
        Assert.AreEqual("rebuild_project_settings_changed", omittedResult.ErrorCode);
        StringAssert.Contains(omittedResult.ErrorMessage, "formatting");
    }

    [TestMethod]
    public void RebuildRefusesEveryMessageSettingMissingFromLegacyXmlThroughClassifierAndPlanner()
    {
        var baseline = Placed();
        PopulateTypedProjectSettings(baseline);
        var missing = new (string What, Action<SchematicMetadata> Remove, Action<SchematicMetadata, SchematicMetadata> Restore)[]
        {
            ("drawing ratios", m => m.DrawingRatios = null, (target, source) => target.DrawingRatios = source.DrawingRatios.Clone()),
            ("formatting", m => m.Formatting = null, (target, source) => target.Formatting = source.Formatting.Clone()),
            ("annotation", m => m.Annotation = null, (target, source) => target.Annotation = source.Annotation.Clone()),
            ("field templates", m => m.FieldTemplates = null, (target, source) => target.FieldTemplates = source.FieldTemplates.Clone()),
            ("symbol comparison", m => m.SymbolComparison = null, (target, source) => target.SymbolComparison = source.SymbolComparison.Clone()),
            ("BOM settings", m => m.BomSettings = null, (target, source) => target.BomSettings = source.BomSettings.Clone()),
            ("net classes", m => m.NetSettings = null, (target, source) => target.NetSettings = source.NetSettings.Clone()),
            ("used references", m => m.ReferenceInventory = null, (target, source) => target.ReferenceInventory = source.ReferenceInventory.Clone()),
            ("net chain classes", m => m.NetChainClasses = null, (target, source) => target.NetChainClasses = source.NetChainClasses.Clone()),
            ("ERC settings", m => m.ErcSettings = null, (target, source) => target.ErcSettings = source.ErcSettings.Clone()),
            ("ngspice settings", m => m.NgspiceSettings = null, (target, source) => target.NgspiceSettings = source.NgspiceSettings.Clone()),
        };
        foreach (var (what, remove, restore) in missing)
        {
            var recorded = baseline with { Schematic = baseline.Schematic.Clone() };
            foreach (var screen in recorded.Schematic.Instances) remove(screen.Metadata);
            var observed = NewEmptyRoot(recorded);
            restore(observed.Instances[0].Metadata, baseline.Schematic.Instances[0].Metadata);
            var state = State(recorded, recorded, observed, "loaded", "created");
            var shape = SchematicRebuild.Classify(state, recorded);
            Assert.AreEqual(SchematicRebuildKind.Rejected, shape.Kind, what);
            Assert.AreEqual("rebuild_project_settings_changed", shape.ErrorCode, what);
            StringAssert.Contains(shape.ErrorMessage, what, what);
            var plan = SchematicSynchronizationPlanner.Plan(state);
            Assert.AreEqual("rebuild_project_settings_changed", plan.ErrorCode, what);
            Assert.IsEmpty(plan.NativeOperations, what + ": no operation may be sent");
            Assert.IsNull(plan.Candidate, what + ": no candidate may be published");

            // The nine newer message groups require a captured native baseline as well as the XML value.
            if (what is not "drawing ratios" and not "formatting")
            {
                var missingNative = NewEmptyRoot(baseline);
                remove(missingNative.Instances[0].Metadata);
                var missingNativeState = State(baseline, baseline, missingNative, "loaded", "created");
                var nativeShape = SchematicRebuild.Classify(missingNativeState, baseline);
                Assert.AreEqual(SchematicRebuildKind.Rejected, nativeShape.Kind, what + ": native coverage");
                Assert.AreEqual("rebuild_project_settings_changed", nativeShape.ErrorCode, what);
                StringAssert.Contains(nativeShape.ErrorMessage, what, what);
                StringAssert.Contains(nativeShape.ErrorMessage, "snapshot did not capture", what);
                var nativePlan = SchematicSynchronizationPlanner.Plan(missingNativeState);
                Assert.AreEqual("rebuild_project_settings_changed", nativePlan.ErrorCode, what);
                Assert.IsEmpty(nativePlan.NativeOperations, what + ": no operation without a native baseline");
                Assert.IsNull(nativePlan.Candidate, what);
            }
        }
    }

    [TestMethod]
    public void EmptyMapAndRepeatedProjectSettingsRemainCaptured()
    {
        var baseline = Placed();
        PopulateTypedProjectSettings(baseline);
        foreach (var screen in baseline.Schematic.Instances)
        {
            screen.Metadata.TextVariables.Clear();
            screen.Metadata.BusAliases.Clear();
            screen.Metadata.VariantDescriptions.Clear();
        }
        var observed = NewEmptyRoot(baseline);
        observed.Instances[0].Metadata.TextVariables["REVISION"] = "B";
        observed.Instances[0].Metadata.BusAliases.Add(new SchematicBusAlias { Name = "REBUILD_BUS", Members = { "D0" } });
        observed.Instances[0].Metadata.VariantDescriptions["Rebuild"] = "variant";
        var state = State(baseline, baseline, observed, "loaded", "created");
        Assert.AreEqual(SchematicRebuildKind.Admitted, SchematicRebuild.Classify(state, baseline).Kind);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsEmpty(plan.NativeOperations.Single(o => o.ReplaceTextVariables is not null).ReplaceTextVariables.Variables);
        Assert.IsEmpty(plan.NativeOperations.Single(o => o.ReplaceBusAliases is not null).ReplaceBusAliases.Aliases);
        Assert.IsEmpty(plan.NativeOperations.Single(o => o.ReplaceVariantRegistry is not null).ReplaceVariantRegistry.Descriptions);
        AssertRebuildCandidate(baseline, plan, observed, "explicit empty project collections");
    }

    [TestMethod]
    public void RebuildAdmitsNgspiceReplacementWhenXmlCapturedIt()
    {
        var baseline = Placed();
        foreach (var screen in baseline.Schematic.Instances)
            screen.Metadata.NgspiceSettings = new SchematicNgspiceSettings { WorkbookFilename = "fixture.raw", FixIncludePaths = true, ModelMode = 2 };
        var kept = NewEmptyRoot(baseline);
        kept.Instances[0].Metadata.NgspiceSettings.ModelMode = 3;
        var state = State(baseline, baseline, kept, "loaded", "created");
        var classified = SchematicRebuild.Classify(state, baseline);
        Assert.AreEqual(SchematicRebuildKind.Admitted, classified.Kind, classified.ErrorMessage);
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        var operation = plan.NativeOperations.Single(o => o.SetNgspiceSettings is not null);
        Assert.AreEqual(2, operation.SetNgspiceSettings.ModelMode);
        Assert.IsTrue(SchematicRebuild.RecreatesFileState(operation, 1));
    }

    // A direct admitted classification still prepares every typed project-setting operation the rebuild journal accepts.
    [TestMethod]
    public void PreparedRebuildAcceptsTypedProjectSettings()
    {
        var baseline = Placed();
        PopulateTypedProjectSettings(baseline);
        var recorded = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in recorded.Schematic.Instances) screen.Metadata.SymbolComparison = new SchematicSymbolComparisonSettings { MissingFields = false, FieldTexts = false };
        var kept = NewEmptyRoot(recorded);
        kept.Instances[0].Metadata.SymbolComparison = new SchematicSymbolComparisonSettings { MissingFields = true, FieldTexts = true };
        var admitted = new SchematicRebuildClassification(SchematicRebuildKind.Admitted, [.. recorded.SheetBindings.Select(b => b.SheetInstanceId)]);
        var state = State(recorded, recorded, kept, "loaded", "created");
        var hierarchy = SchematicHierarchyMerge.Plan(recorded.Schematic, recorded.Schematic, kept);
        var plan = SchematicRebuild.Prepare(state, recorded, hierarchy, admitted, []);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.AreEqual(recorded.Schematic.Instances[0].Metadata.SymbolComparison,
            plan.NativeOperations.Single(o => o.SetSymbolComparison is not null).SetSymbolComparison);
        Assert.IsNotNull(plan.Rebuild);

        // Precision: the same prepared rebuild with the kept root's symbol-comparison settings equal to the XML's is planned,
        // and its batch sets no symbol-comparison policy.
        var same = NewEmptyRoot(recorded);
        var agreed = SchematicRebuild.Prepare(State(recorded, recorded, same, "loaded", "created"), recorded,
            SchematicHierarchyMerge.Plan(recorded.Schematic, recorded.Schematic, same), admitted, []);
        Assert.IsTrue(agreed.CanPrepare, agreed.ErrorCode + ": " + agreed.ErrorMessage);
        Assert.IsTrue(agreed.NativeRebuildRequired);
        Assert.IsFalse(agreed.NativeOperations.Any(o => o.SetSymbolComparison is not null));
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
        foreach (var forbidden in new[]
        {
            new SchematicItemOperation { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } },
            new SchematicItemOperation { MoveConnectedSymbols = new() },
            new SchematicItemOperation { TransformConnectedSymbols = new() },
            new SchematicItemOperation { SetSymbolLocks = new() },
            new SchematicItemOperation { AssertConnectivity = new() { Version = 1 } },
        })
        {
            Assert.IsFalse(SchematicRebuild.RecreatesFileState(forbidden, 0), forbidden.OperationCase.ToString());
            Assert.IsFalse(SchematicRebuild.RecreatesFileState(forbidden, 1), forbidden.OperationCase.ToString());
            var candidate = batch.Clone(); candidate.Operations.Add(forbidden);
            Assert.IsFalse(SchematicRebuild.IsRebuild(state with { PendingMutation = candidate,
                PendingLayout = Layout(DesignLayoutIntent.RebuildLane) }), forbidden.OperationCase.ToString());
        }
        var other = batch.Clone(); other.Description = "Apply XML synchronization candidate";
        Assert.IsFalse(SchematicRebuild.IsRebuild(state with { PendingMutation = other, PendingLayout = Layout(DesignLayoutIntent.RebuildLane) }));
        Assert.AreEqual("cpu_power", SchematicRebuild.GeneratedFileStem("CPU_POWER"));
        Assert.AreEqual("dc-dc_stage_2", SchematicRebuild.GeneratedFileStem(" DC-DC stage/2 "));
    }

    // XML removal of units and components (ledger p74ee7c1da24272d9), the isolated rules behind the hook the PSU/CPU
    // ownership journey (NativeSymbolSheetOwnershipJourney, check ownership-sync) drives through KiCad: which XML shapes are
    // removals, the exact native removals they plan, and the guards that keep other shapes on the general path or keep both
    // versions. Extending an existing test was not possible: no test covered XML-side removal before this item.
    // The XML an author writes to remove these occurrences: whole components go with their definitions and net pins, and a
    // component that keeps other units loses from its nets the pins only the removed units draw.
    internal static SchematicDesign WithoutOccurrences(SchematicDesign design, params Guid[] occurrences)
    {
        var circuit = design.Engineering.Circuit;
        var retired = circuit.Components.Where(c => circuit.Symbols.Where(s => s.ComponentId == c.Id).All(s => occurrences.Contains(s.Id)))
            .Select(c => c.Id).ToHashSet();
        var definitions = circuit.Components.Where(c => retired.Contains(c.Id)).Select(c => c.DefinitionId).ToHashSet();
        var parts = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id, d => circuit.Parts.Single(p => p.Id == d.PartId));
        var unitPins = new HashSet<PinEndpoint>();
        foreach (var component in circuit.Components.Where(c => !retired.Contains(c.Id)))
        {
            var kept = circuit.Symbols.Where(s => s.ComponentId == component.Id && !occurrences.Contains(s.Id)).Select(s => s.Unit).ToHashSet();
            if (kept.Count == circuit.Symbols.Count(s => s.ComponentId == component.Id)) continue;
            foreach (var pins in parts[component.DefinitionId].Pins.GroupBy(p => p.Number, StringComparer.Ordinal))
                if (!pins.Any(p => p.Unit == 0 || kept.Contains(p.Unit))) unitPins.Add(new(component.Id, pins.Key));
        }
        return design with
        {
            Engineering = design.Engineering with { Circuit = circuit with
            {
                Symbols = [.. circuit.Symbols.Where(s => !occurrences.Contains(s.Id))],
                Components = [.. circuit.Components.Where(c => !retired.Contains(c.Id))],
                Sheets = [.. circuit.Sheets.Select(s => s with { Components = [.. s.Components.Where(c => !definitions.Contains(c.Id))] })],
                Nets = [.. circuit.Nets.Select(n => n with { Pins = [.. n.Pins.Where(p => !retired.Contains(p.ComponentId) && !unitPins.Contains(p))] })]
            } },
            SymbolBindings = [.. design.SymbolBindings.Where(b => !occurrences.Contains(b.SymbolOccurrenceId))]
        };
    }

    [TestMethod]
    public void XmlThatRemovesAUnitAndAComponentRemovesExactlyTheirSymbolsInKiCad()
    {
        var baseline = Placed();
        Guid unitFour = PsuCpuIds.Id(0x09, 10), memory = PsuCpuIds.Id(0x09, 11);
        var desired = WithoutOccurrences(baseline, unitFour, memory);
        var state = State(baseline, desired);
        var shape = SchematicRebuild.Classify(state, desired);
        Assert.AreEqual(SchematicRebuildKind.Admitted, shape.Kind, shape.ErrorCode + ": " + shape.ErrorMessage);
        var natives = new[] { unitFour, memory }.Select(o => baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == o).NativeObjectId.ToString("D")).ToArray();
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsNull(plan.Rebuild, "A removal is an ordinary synchronization, not a rebuild.");
        CollectionAssert.AreEquivalent(natives, plan.NativeOperations.Where(o => o.Remove is not null).Select(o => o.Remove.Value).ToArray(),
            "Only the two symbols are removed.");
        Assert.IsTrue(plan.NativeOperations.Where(o => o.Remove is null).All(o => o.ReplaceLibraryCache is not null),
            "Besides the removals, only the touched screens' library caches are stated, unchanged.");
        CollectionAssert.AreEquivalent(new[] { unitFour, memory }, plan.Electrical!.RemovedSymbolOccurrences!.ToArray());
        var candidate = plan.Candidate!;
        Assert.IsFalse(candidate.Engineering.Circuit.Components.Any(c => c.Id == PsuCpuIds.Id(0x07, 8)), "U6 is gone.");
        Assert.IsTrue(candidate.Engineering.Circuit.Components.Any(c => c.Id == PsuCpuIds.Id(0x07, 7)), "U5 keeps its other units.");
        Assert.IsFalse(candidate.Schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Any(i => natives.Contains(i.Unpack<SchematicSymbolInstance>().Id.Value)), "The candidate draws neither symbol.");
        Assert.IsTrue(SchematicDesignBindings.Inspect(candidate, []).IdentitiesResolved);
        Assert.AreEqual(plan.CandidateXml, SchematicDesignXml.Write(candidate, []));
        // Planning is pure: the same state plans the same removal again.
        CollectionAssert.AreEqual(plan.NativeOperations.ToArray(), SchematicSynchronizationPlanner.Plan(state).NativeOperations.ToArray());
    }

    [TestMethod]
    public void OtherXmlShapesKeepTheGeneralPathAndAConcurrentKiCadChangeKeepsBothVersions()
    {
        var baseline = Placed();
        Guid memory = PsuCpuIds.Id(0x09, 11);
        var removal = WithoutOccurrences(baseline, memory);
        // Must-not-claim: XML that drops the occurrence but keeps its native binding is not a consistent removal.
        var inconsistent = removal with { SymbolBindings = baseline.SymbolBindings };
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, inconsistent), inconsistent).Kind);
        Assert.AreEqual("electrical_ownership_changed", SchematicSynchronizationPlanner.Plan(State(baseline, inconsistent)).ErrorCode);
        // Must-not-claim: a removal together with another XML edit.
        var renamed = removal with { Engineering = removal.Engineering with { Circuit = removal.Engineering.Circuit with
        { Components = [.. removal.Engineering.Circuit.Components.Select(c => c.Id == PsuCpuIds.Id(0x07, 1) ? c with { Reference = "J9" } : c)] } } };
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, renamed), renamed).Kind);
        Assert.AreEqual(SchematicRebuildKind.NotApplicable, SchematicRebuild.Classify(State(baseline, baseline), baseline).Kind, "Nothing removed.");
        // Must-catch: KiCad moved a symbol since the last synchronization; neither version is applied.
        var moved = baseline.Schematic.Clone();
        var screen = moved.Instances.First(s => s.Items.Any(i => i.Is(SchematicSymbolInstance.Descriptor)));
        int index = screen.Items.ToList().FindIndex(i => i.Is(SchematicSymbolInstance.Descriptor));
        var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>(); symbol.Position.XNm += 2_540_000; screen.Items[index] = Any.Pack(symbol);
        var both = State(baseline, removal, moved);
        var refused = SchematicRebuild.Classify(both, removal);
        Assert.AreEqual(SchematicRebuildKind.Rejected, refused.Kind);
        Assert.AreEqual("ownership_change_with_xml_edits", refused.ErrorCode);
        var plan = SchematicSynchronizationPlanner.Plan(both);
        Assert.AreEqual("ownership_change_with_xml_edits", plan.ErrorCode);
        Assert.IsEmpty(plan.NativeOperations); Assert.IsNull(plan.CandidateXml);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(SchematicDesignXml.Write(removal, [])), both.DesiredFileBytes, "The XML version is kept.");
    }
}
