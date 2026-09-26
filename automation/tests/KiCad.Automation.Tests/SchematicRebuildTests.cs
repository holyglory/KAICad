using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ErcErrorType = Kiapi.Schematic.ErcErrorType;
using ErcSeveritySetting = Kiapi.Schematic.ErcSeveritySetting;
using NetClass = Kiapi.Common.Project.NetClass;

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
        // KiCad names the untyped project settings, shared-screen root ownership and net chains on every snapshot. A design
        // with no sheet file shown twice and no net chain loses nothing to the latter two, and the project settings stay in
        // the kept project file: it is rebuilt.
        var covered = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in covered.Schematic.Instances)
            screen.Metadata.UnrepresentedState.Add(new[] { SchematicRebuild.RetainedProjectSettings, "shared_screen_root_ownership", "net_chains" });
        Assert.AreEqual(SchematicRebuildKind.Admitted, SchematicRebuild.Classify(State(covered, covered, NewEmptyRoot(covered), "loaded", "created"), covered).Kind);
        // Must-catch: net chains are not rebuilt yet, and a limitation the XML cannot answer is a loss.
        var chained = covered with { Schematic = covered.Schematic.Clone() };
        foreach (var screen in chained.Schematic.Instances) screen.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "DATA_PATH",
            From = new() { Reference = "U1", Pin = "1" }, To = new() { Reference = "U2", Pin = "1" }, MemberNets = { "/DATA" } });
        var withChains = SchematicRebuild.Classify(State(chained, chained, NewEmptyRoot(chained), "loaded", "created"), chained);
        Assert.AreEqual("rebuild_state_unrepresented", withChains.ErrorCode, withChains.ErrorMessage);
        StringAssert.Contains(withChains.ErrorMessage, "net_chains");
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
        // Must-catch: shared ownership is lost when a sheet file is shown by two sheets, or when there is a second root.
        Assert.IsFalse(SchematicRebuild.Lost("shared_screen_root_ownership", covered.Schematic));
        var repeated = covered.Schematic.Clone();
        var child = repeated.Instances.First(s => !s.Metadata.Document.Equals(repeated.Document)).Clone();
        child.Metadata.Document.SheetPath.Path[^1] = new KIID { Value = Guid.NewGuid().ToString("D") };
        repeated.Instances.Add(child);
        Assert.IsTrue(SchematicRebuild.Lost("shared_screen_root_ownership", repeated));
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

    // Review finding (lane 2C, xml-rebuild): the kept project file must not be overwritten. A rebuild whose new root shows
    // project settings other than the XML's is refused before anything reaches KiCad, and no rebuild journal holds a
    // project setting. Every one of the thirteen setting groups the project file holds is checked on its own, with the
    // exact message naming only that group (ledger p001c485926b37099). The message names what is compared (the settings
    // KiCad shows) and only actions that work while the files are lost; synchronizing the settings into the XML is not one
    // of them (review of 31cbe4f594). The NativeXmlRebuild journey proves the refusal through kicad_design_sync_plan and
    // kicad_design_sync_apply on a setting changed in KiCad, and apply's refusal of a project file changed on disk.
    private const string SettingsChangedMessage = "differ from the ones the XML records, so rebuilding would overwrite them. "
        + "Put them back as the XML records them (change them back in KiCad, or restore the project file KiCad last saved with "
        + "this XML and reopen the project), then rebuild.";

    private static readonly (string What, Action<SchematicMetadata> Edit)[] ProjectSettingGroups =
    [
        ("text variables", m => m.TextVariables["REVISION"] = "B"),
        ("bus aliases", m => m.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } })),
        ("variants", m => m.VariantDescriptions["Lite"] = "Without telemetry"),
        ("drawing ratios", m => m.DrawingRatios = new SchematicDrawingRatios { DashLengthRatio = 12, GapLengthRatio = 3, TextOffsetRatio = 0.15,
            LabelSizeRatio = 0.375, OverbarHeightRatio = 1.23 }),
        ("formatting", m => m.Formatting = Formatting(1_524_000)),
        ("annotation", m => m.Annotation = new SchematicAnnotationSettings { StartAfter = 100 }),
        ("field templates", m => m.FieldTemplates = new SchematicFieldTemplates { Fields = { new SchematicFieldTemplate { Name = "Supplier", Visible = true } } }),
        ("symbol comparison", m => m.SymbolComparison = new SchematicSymbolComparisonSettings { MissingFields = true, FieldTexts = true }),
        ("BOM settings", m => m.BomSettings = new SchematicBomSettings { ExportFilename = "fixture-bom.csv" }),
        ("net classes", m => m.NetSettings = new SchematicNetSettings { DefaultClass = new NetClass { Name = "Default" } }),
        ("used references", m => m.ReferenceInventory = new SchematicReferenceInventory { Allocated = { "R7" } }),
        ("net chain classes", m => m.NetChainClasses = new SchematicNetChainClassState { Definitions = { "fastbus" } }),
        ("ERC settings", m => m.ErcSettings = new SchematicErcSettings { RuleSeverities =
            { new ErcSeveritySetting { RuleType = ErcErrorType.ErcetPinNotConnected, Severity = RuleSeverity.RsError } } }),
    ];

    // A complete, valid formatting replacement (SchematicFormatting.Validate) with the given default text size.
    private static SchematicFormattingSettings Formatting(long textSizeNm) => new()
    {
        DefaultLineWidthNm = 152_400, DefaultTextSizeNm = textSizeNm, PinSymbolSizeNm = 635_000, ConnectionGridNm = 1_270_000,
        JunctionSizeChoice = 3, HopOverSizeChoice = 0,
        OperatingPoint = new() { VoltagePrecision = 3, VoltageRange = "~V", CurrentPrecision = 3, CurrentRange = "~A" },
        UnitReference = new() { SeparatorAscii = 0, FirstIdAscii = 'A' }
    };

    [TestMethod]
    public void RebuildRefusesAKeptProjectFileWhoseSettingsDifferFromTheXml()
    {
        var baseline = Placed();
        Assert.HasCount(13, ProjectSettingGroups, "Every typed setting group the project file holds (SchematicRebuild.ChangedProjectSettings).");
        foreach (var (what, edit) in ProjectSettingGroups)
        {
            var kept = NewEmptyRoot(baseline);
            edit(kept.Instances[0].Metadata);
            var state = State(baseline, baseline, kept, "loaded", "created");
            var shape = SchematicRebuild.Classify(state, baseline);
            Assert.AreEqual(SchematicRebuildKind.Rejected, shape.Kind, what);
            Assert.AreEqual("rebuild_project_settings_changed", shape.ErrorCode, what);
            Assert.AreEqual("KiCad's project settings (" + what + ") " + SettingsChangedMessage, shape.ErrorMessage, what);
            var plan = SchematicSynchronizationPlanner.Plan(state);
            Assert.AreEqual("rebuild_project_settings_changed", plan.ErrorCode, what);
            Assert.AreEqual(shape.ErrorMessage, plan.ErrorMessage, what);
            Assert.IsEmpty(plan.NativeOperations, what + ": nothing is sent to KiCad.");
            Assert.IsNull(plan.Candidate, what + ": nothing is published.");
        }
        // All of them at once are named together, in the order the project file's groups are compared.
        var all = NewEmptyRoot(baseline);
        foreach (var (_, edit) in ProjectSettingGroups) edit(all.Instances[0].Metadata);
        Assert.AreEqual("KiCad's project settings (" + string.Join(", ", ProjectSettingGroups.Select(g => g.What)) + ") " + SettingsChangedMessage,
            SchematicRebuild.Classify(State(baseline, baseline, all, "loaded", "created"), baseline).ErrorMessage);
        // Precision: the schematic file's own state on the new root (here its page) is what a rebuild recreates.
        var fresh = NewEmptyRoot(baseline);
        Assert.IsNotNull(fresh.Instances[0].Metadata.Page);
        fresh.Instances[0].Metadata.Page = new PageSettings { PageSize = PageSize.PsA2 };
        var recreated = SchematicSynchronizationPlanner.Plan(State(baseline, baseline, fresh, "loaded", "created"));
        Assert.IsTrue(recreated.CanPrepare, recreated.ErrorCode + ": " + recreated.ErrorMessage);
        Assert.AreEqual(1, recreated.NativeOperations.Count(o => o.SetPageSettings is not null && o.TargetDocument.Equals(baseline.Schematic.Document)));
        // No project setting ever enters a rebuild journal.
        foreach (var setting in new SchematicItemOperation[]
        {
            new() { ReplaceTextVariables = new() }, new() { SetFormatting = new() }, new() { SetNetSettings = new() },
            new() { SetErcSettings = new() }, new() { SetBomSettings = new() }, new() { SetAnnotation = new() },
            new() { SetFieldTemplates = new() }, new() { SetSymbolComparison = new() }, new() { ReplaceNetChainClasses = new() },
            new() { SetDrawingRatios = new() }, new() { SetReferenceInventory = new() }, new() { ReplaceVariantRegistry = new() },
            new() { ReplaceBusAliases = new() }, new() { ReplaceNetChains = new() },
        })
            Assert.IsFalse(SchematicRebuild.RecreatesFileState(setting, 1), setting.OperationCase.ToString());
        foreach (var file in new SchematicItemOperation[] { new() { SetPageSettings = new() }, new() { SetTitleBlock = new() },
            new() { SetRootInstance = new() }, new() { ReplaceEmbeddedFiles = new() } })
            Assert.IsTrue(SchematicRebuild.RecreatesFileState(file, 1), file.OperationCase.ToString());
    }

    // Ledger p001c485926b37099: the second line of defence behind the settings check. Prepare refuses any planned batch that
    // holds an edit a rebuild never makes, here the project formatting the kept root shows differently from the XML. The
    // planner never reaches it this way (classification refuses the same state first, as asserted), so Prepare is called
    // directly with an admitted classification; no journey can reach it without bypassing classification. Unit test,
    // because no existing test prepares an admitted rebuild whose batch the journal would not admit.
    [TestMethod]
    public void PreparedRebuildRefusesABatchWithAnEditARebuildNeverMakes()
    {
        var baseline = Placed();
        var recorded = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in recorded.Schematic.Instances) screen.Metadata.Formatting = Formatting(1_270_000);
        var kept = NewEmptyRoot(recorded);
        kept.Instances[0].Metadata.Formatting = Formatting(1_524_000);
        var state = State(recorded, recorded, kept, "loaded", "created");
        var classified = SchematicRebuild.Classify(state, recorded);
        Assert.AreEqual("rebuild_project_settings_changed", classified.ErrorCode, "Classification refuses this state first.");
        Assert.AreEqual("KiCad's project settings (formatting) " + SettingsChangedMessage, classified.ErrorMessage);

        var admitted = new SchematicRebuildClassification(SchematicRebuildKind.Admitted, [.. recorded.SheetBindings.Select(b => b.SheetInstanceId)]);
        var hierarchy = SchematicHierarchyMerge.Plan(recorded.Schematic, recorded.Schematic, kept);
        var plan = SchematicRebuild.Prepare(state, recorded, hierarchy, admitted, []);
        Assert.AreEqual("rebuild_operation_unsupported", plan.ErrorCode, plan.ErrorMessage);
        Assert.AreEqual("Rebuilding these sheets from the XML would need an edit a rebuild never makes; nothing was sent to KiCad.", plan.ErrorMessage);
        Assert.IsEmpty(plan.NativeOperations, "No native operation is planned.");
        Assert.IsNull(plan.Candidate);
        Assert.IsNull(plan.CandidateXml);
        Assert.IsNull(plan.Rebuild, "No rebuild intent reaches the executor.");
        Assert.IsFalse(plan.CanPrepare);

        // Precision: the same prepared rebuild with the kept root's formatting equal to the XML's is planned, and its batch
        // sets no project setting.
        var same = NewEmptyRoot(recorded);
        var agreed = SchematicRebuild.Prepare(State(recorded, recorded, same, "loaded", "created"), recorded,
            SchematicHierarchyMerge.Plan(recorded.Schematic, recorded.Schematic, same), admitted, []);
        Assert.IsTrue(agreed.CanPrepare, agreed.ErrorCode + ": " + agreed.ErrorMessage);
        Assert.IsTrue(agreed.NativeRebuildRequired);
        Assert.IsFalse(agreed.NativeOperations.Any(o => o.SetFormatting is not null));
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
