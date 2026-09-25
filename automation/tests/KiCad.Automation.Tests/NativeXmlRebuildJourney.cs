using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9), lane 2C, ledger p168f8654d86c5a1a.
    //
    // A person's project whose schematic files are lost is rebuilt from its design XML, through the public MCP tools of
    // the production server over STDIO, with nothing lost. From the S2 seed (a root sheet only):
    //  1. The fixture's four sheets are generated from XML that adds them (sheet generation), the CPU sheet's paper is
    //     set to A3 in the XML, and the fixture's eight components are created on their sheets from XML with a proposed
    //     layout. Three of the fixture's nets are then drawn in KiCad and published to the XML: two with local labels on
    //     one sheet each, and one across sheets with hierarchical labels, sheet pins and a wire on the root. This is the
    //     original project, saved by KiCad.
    //  2. Every schematic file is deleted, KiCad creates a new empty root for the project, the recovery record adopts it,
    //     and apply rebuilds the schematic from the XML last synchronized with KiCad.
    //  3. The rebuilt schematic is the original on every captured state group: the whole-document state digest (every
    //     screen as KiCad saves it plus the project settings) and each saved file byte for byte, with the same identities
    //     (screens, sheets, symbols, pins), sheets, library caches, pin partition and settings. A second apply is a
    //     no-op, and one native undo returns to the empty root while redo restores the rebuild.
    // The fixture's Complete stage adds all eleven nets in XML; KiCad can draw XML nets only through lane 2A's connection
    // realization, so this journey's original holds the Components stage plus three nets drawn in KiCad, whether or not
    // an editor advertises it. The rebuild recreates whatever objects the XML holds: labels, wires, and sheet symbols with
    // their sheet pins, each compared exactly with the original.
    // The native rules of the root identity a rebuild adopts are exercised here too, in the live editor: a root KiCad
    // loaded from its file, an identity that is not first or not canonical, and a batch that fails after the identity
    // (which must leave the root's own identity) are refused with nothing changed.
    private static async Task VerifyPsuCpuXmlRebuild(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        const PsuCpuStage Stage = PsuCpuStage.Components;
        var expected = PsuCpuFixture.ExpectedNative(Stage);
        string path = context.DesignPath;
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-xml-rebuild-" + name);
        var store = new DesignRecoveryStore(Evidence("recovery.json"));
        var document = context.Root;
        Assert.AreEqual(PsuCpuSeed.RootOnly, context.Seed);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(), Evidence("host"), Evidence("host.log"), token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        object Recovery(string revision) => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = revision };
        var steps = new List<object>();

        // ---- 1. The original project, built from XML ----------------------------------------------------------
        var saved = await PsuCpuFixture.InitializeRecoveryAsync(client, context, store.StatePath, token);
        var seed = await Capture();
        Assert.HasCount(1, seed.Electrical.Hierarchy.Data.Instances, "S2 holds the root sheet only.");
        // Must-catch: a root KiCad loaded from its file never adopts another identity, even when it is empty.
        Assert.IsEmpty(seed.Electrical.Hierarchy.Data.Instances[0].Items);
        var refusals = new List<object> { await RefusedIdentity(seed, "never loaded from or saved to a file", Identity(Guid.NewGuid().ToString("D"))) };

        // 1a. Sheet generation: the XML adds the three sheets below the root and nothing else.
        var sheetsOnly = PsuCpuFixture.Desired(context, PsuCpuStage.SheetsOnly);
        Assert.HasCount(1, sheetsOnly.SheetBindings, "Only the root is bound; the new sheets have no native counterpart yet.");
        saved = await Desire(saved, sheetsOnly);
        var generationPlan = await Plan(saved, "generation-plan");
        Assert.IsTrue(generationPlan.GetProperty("nativeRebuildRequired").GetBoolean(), generationPlan.GetRawText());
        var generated = new[] { 2, 3, 4 }.Select(n => SchematicRebuild.GeneratedSheet(PsuCpuIds.Id(0x05, n))).ToArray();
        CollectionAssert.AreEqual(generated.Select(g => g.SheetInstanceId.ToString("D")).ToArray(),
            generationPlan.GetProperty("rebuildSheetInstances").EnumerateArray().Select(e => e.GetString()).ToArray());
        var generationOperations = Operations(generationPlan);
        Assert.AreEqual(3, generationOperations.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true), "One sheet symbol per new sheet.");
        Assert.AreEqual(seed, await Capture(), "Planning must not change KiCad.");
        var afterGeneration = await Apply(saved, "generation");
        var generatedDesign = store.Read()!.State.Baseline;
        var generatedNative = await Capture();
        Assert.HasCount(4, generatedNative.Electrical.Hierarchy.Data.Instances, "KiCad shows the root and the three generated sheets.");
        foreach (var (identity, sheet) in generated.Zip(expected.Sheets.Where(s => s.Parent is not null)))
        {
            var binding = generatedDesign.SheetBindings.Single(b => b.SheetInstanceId == identity.SheetInstanceId);
            Assert.AreEqual(identity.SheetSymbolId, binding.NativePath[^1], sheet.Key + " is bound to its generated sheet symbol.");
            var screen = generatedNative.Electrical.Hierarchy.Data.Instances.Single(s => RebuildPathKey(s) == string.Join('/', binding.NativePath.Select(p => p.ToString("D"))));
            Assert.AreEqual(identity.ScreenId.ToString("D"), screen.Metadata.ScreenId.Value, sheet.Key + " shows its generated screen.");
            var children = generated.Where(g => generatedDesign.SheetBindings.Single(b => b.SheetInstanceId == g.SheetInstanceId).NativePath.SkipLast(1)
                .SequenceEqual(binding.NativePath)).Select(g => g.SheetSymbolId.ToString("D")).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(children, screen.Items.Select(i => i.Unpack<SheetSymbol>().Id.Value).Order(StringComparer.Ordinal).ToArray(),
                sheet.Key + " is generated holding only the sheet symbols of its generated child sheets.");
            var parent = generatedNative.Electrical.Hierarchy.Data.Instances.Single(s => RebuildPathKey(s) == string.Join('/', binding.NativePath.SkipLast(1).Select(p => p.ToString("D"))));
            var symbol = parent.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Value == identity.SheetSymbolId.ToString("D"));
            Assert.AreEqual(sheet.SheetName, symbol.NameField.Text.Text_);
            Assert.AreEqual(sheet.File, symbol.FilenameField.Text.Text_, "The generated file is named after the sheet.");
            Assert.AreEqual(sheet.Page, symbol.PageNumber);
        }
        steps.Add(new { step = "generation", operations = generationOperations.Count, afterGeneration });

        // 1b. The CPU sheet holds three units of a 177-pin processor, so the XML gives it an A3 page (contract §1.6.2).
        var paper = generatedDesign with { Schematic = generatedDesign.Schematic.Clone() };
        var cpuPath = string.Join('/', paper.SheetBindings.Single(b => b.SheetInstanceId == PsuCpuIds.Id(0x05, 3)).NativePath.Select(p => p.ToString("D")));
        paper.Schematic.Instances.Single(s => RebuildPathKey(s) == cpuPath).Metadata.Page.PageSize = PageSize.PsA3;
        saved = await Desire(store.Read()!, paper);
        var paperPlan = await Plan(saved, "paper-plan");
        Assert.IsFalse(paperPlan.GetProperty("nativeRebuildRequired").GetBoolean(), "A page edit is an ordinary synchronization.");
        steps.Add(new { step = "paper", afterPaper = await Apply(saved, "paper") });

        // 1c. The fixture's components on their sheets, laid out by the public layout proposal.
        var published = store.Read()!.State.Baseline;
        var components = PsuCpuFixture.Engineering(Stage);
        var parts = components.Circuit.Parts.Select(p => p.Id).ToHashSet();
        var withComponents = published with { Engineering = components, PartSymbols = [.. context.PartSymbols.Where(s => parts.Contains(s.PartId))] };
        saved = await Desire(store.Read()!, withComponents);
        var regions = new List<SchematicLayoutRegion>();
        var measured = await Capture();
        foreach (var sheet in expected.Sheets.Where(s => expected.Symbols.Any(x => x.Sheet == s.Key)))
        {
            var bound = string.Join('/', published.SheetBindings.Single(b => b.SheetInstanceId == sheet.ModelSheetInstance).NativePath.Select(p => p.ToString("D")));
            var screen = measured.Electrical.Hierarchy.Data.Instances.Single(s => RebuildPathKey(s) == bound);
            var page = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = screen.Metadata.Document.Clone(), ExpectedRevision = measured.State.Revision.Clone() }, token);
            long inset = expected.Presentation.PageInsetMm * 1_000_000L, reserve = expected.Presentation.ReservedBottomMm * 1_000_000L;
            regions.Add(new(Guid.Parse(screen.Metadata.ScreenId.Value), new PresentationBounds(page.PageBounds.Position.XNm + inset,
                page.PageBounds.Position.YNm + inset, page.PageBounds.Position.XNm + page.PageBounds.Size.XNm - inset,
                page.PageBounds.Position.YNm + page.PageBounds.Size.YNm - reserve), []));
        }
        var layout = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = saved.RevisionToken, gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L, regions,
            userInstructions = "Place each fixture component on its own sheet and keep the processor's power unit on CPU_POWER." });
        await File.WriteAllTextAsync(Evidence("initial-layout.json"), RetainedToolEvidence(layout), token);
        RequireToolSuccess(layout);
        byte[] laidOut = Encoding.UTF8.GetBytes(layout.GetProperty("structuredContent").GetProperty("desiredXml").GetString()!);
        await File.WriteAllBytesAsync(path, laidOut, token);
        saved = store.Save(saved.State with { DesiredFileBytes = laidOut }, saved.RevisionToken);
        var creationPlan = await Plan(saved, "creation-plan");
        Assert.IsFalse(creationPlan.GetProperty("nativeRebuildRequired").GetBoolean(), "Component creation is lane 2A's creation path.");
        steps.Add(new { step = "components", afterCreation = await Apply(saved, "creation") });
        var placed = await Capture();
        PsuCpuFixture.AssertNative(store.Read()!.State.Baseline, placed.Electrical, Stage);

        // 1d. Connections drawn in KiCad, as a person would: a local label on each pin of the fixture's VIN net (J1.1 and
        // U1.2 on PSU) and MEM_SCL net (U5.161 and U6.6 on CPU), at the pin's connection point and facing away from it;
        // and the TELEM_MCU_TO_CPU net across sheets (U4.8 on PSU, U5.74 on CPU): a hierarchical label on each pin, a sheet
        // pin of that name on the PSU and CPU sheet symbols, and a wire joining the two sheet pins on the root. The next
        // synchronization publishes them to the XML as nets, so the rebuild must restore labels, sheet pins, the wire and
        // the connections.
        const string Crossing = "TELEM_MCU_TO_CPU";
        var wired = new List<(string Net, string[] Pins)> { ("VIN", ["J1.1", "U1.2"]), ("MEM_SCL", ["U5.161", "U6.6"]), (Crossing, ["U4.8", "U5.74"]) };
        var labelled = await DrawConnections(placed, [(2, "VIN", ["J1.1", "U1.2"], ConnectionLabelKind.Local),
            (3, "MEM_SCL", ["U5.161", "U6.6"], ConnectionLabelKind.Local), (2, Crossing, ["U4.8"], ConnectionLabelKind.Hierarchical),
            (3, Crossing, ["U5.74"], ConnectionLabelKind.Hierarchical)], Crossing);
        var refresh = await host.Tool("kicad_design_recovery_refresh", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken });
        RequireToolSuccess(refresh);
        saved = store.Read()!;
        var wiringPlan = await Plan(saved, "wiring-plan");
        Assert.IsFalse(wiringPlan.GetProperty("nativeRebuildRequired").GetBoolean(), "Publishing KiCad's own edits is an ordinary synchronization.");
        Assert.AreEqual(0, wiringPlan.GetProperty("nativeOperationsJson").GetArrayLength(), "KiCad already shows the connections.");
        steps.Add(new { step = "wiring", labels = labelled, afterWiring = await Apply(saved, "wiring", nativeEdit: false) });

        // The original: saved by KiCad, clean, and the XML settled on it.
        var original = await Capture();
        Assert.IsFalse(original.State.NativeContentDirty, "Apply saved the original.");
        var originalDesign = store.Read()!.State.Baseline;
        var originalReferences = originalDesign.Engineering.Circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
        CollectionAssert.AreEquivalent(wired.Select(w => string.Join(",", w.Pins.Order(StringComparer.Ordinal))).ToArray(),
            originalDesign.Engineering.Circuit.Nets.Select(n => string.Join(",", n.Pins.Select(p => originalReferences[p.ComponentId] + "." + p.Pin)
                .Order(StringComparer.Ordinal))).ToArray(), "The XML holds exactly the three connections drawn in KiCad.");
        var originalSheetPins = OriginalSheetPins(original.Electrical.Hierarchy.Data);
        Assert.HasCount(2, originalSheetPins, "The PSU and CPU sheet symbols each hold the crossing's sheet pin.");
        // KiCad names what no snapshot holds completely; this schematic has no sheet file shown twice and no net chain, so
        // only the untyped project settings are not in the XML, and they stay in the kept project file.
        foreach (var screen in original.Electrical.Hierarchy.Data.Instances)
        {
            CollectionAssert.AreEqual(new[] { SchematicRebuild.RetainedProjectSettings, "shared_screen_root_ownership", "net_chains" },
                screen.Metadata.UnrepresentedState.ToArray(), "The snapshot's coverage list is the same for every schematic.");
            Assert.IsEmpty(screen.Metadata.NetChains);
        }
        Assert.IsFalse(SchematicRebuild.Lost("shared_screen_root_ownership", original.Electrical.Hierarchy.Data));
        string projectFile = Path.Combine(context.ProjectDirectory, "fixture.kicad_pro");
        var schematicFiles = new[] { "fixture.kicad_sch", "psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" }
            .Select(name => Path.Combine(context.ProjectDirectory, name)).ToArray();
        var originalFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string file in schematicFiles.Append(projectFile))
        {
            originalFiles[file] = await File.ReadAllBytesAsync(file, token);
            await File.WriteAllBytesAsync(Evidence("original-" + Path.GetFileName(file)), originalFiles[file], token);
        }
        byte[] originalXml = await File.ReadAllBytesAsync(path, token);
        await File.WriteAllTextAsync(Evidence("original.xml"), SchematicDataXml.Write(original.Electrical.Hierarchy.Data), token);

        // ---- 2. Every schematic file is lost and rebuilt from the XML ----------------------------------------------
        var close = await host.Tool("kicad_document_close", new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(original.State),
            operationId = Guid.NewGuid().ToString("D") });
        await File.WriteAllTextAsync(Evidence("close.json"), close.GetRawText(), token);
        RequireToolSuccess(close);
        foreach (string file in schematicFiles) File.Delete(file);
        Assert.IsFalse(schematicFiles.Any(File.Exists), "Every schematic file is gone.");
        var created = await host.Tool("kicad_schematic_create", new { instanceId, path = schematicFiles[0] });
        RequireToolSuccess(created);
        var reopened = SchematicJson.Parser.Parse<DocumentSpecifier>(created.GetProperty("content").EnumerateArray()
            .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
        Assert.AreEqual(document, reopened, "KiCad creates the project's root with the root identity the project declares.");
        var empty = await Until("the new empty root", s => s.Electrical.Hierarchy.Data.Instances.Count == 1);
        var emptyRoot = empty.Electrical.Hierarchy.Data.Instances.Single();
        Assert.IsEmpty(emptyRoot.Items);
        Assert.AreEqual(0u, emptyRoot.Metadata.LoadedNativeFormatVersion, "The new root was never loaded from a file.");
        Assert.AreNotEqual(original.State.Revision.Epoch, empty.State.Revision.Epoch, "The new root is a new document session.");
        string originalRootScreen = original.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.ScreenId.Value;
        Assert.AreNotEqual(originalRootScreen, emptyRoot.Metadata.ScreenId.Value, "KiCad gives the new root a new screen identity.");
        CollectionAssert.AreEqual(originalXml, await File.ReadAllBytesAsync(path, token), "Losing the schematic files leaves the XML untouched.");

        // Must-catch, in the live editor through the checked batch path apply uses: the identity is only ever a batch's
        // first operation and one canonical UUID, and a batch that fails after it leaves the root the identity KiCad gave it.
        var probeLabel = new LocalLabel { Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Position = new Vector2(), Text = new Text { Text_ = "PROBE" } };
        refusals.Add(await RefusedIdentity(empty, "must be the first operation",
            new SchematicItemOperation { TargetDocument = document.Clone(), SetTitleBlock = emptyRoot.Metadata.TitleBlock?.Clone() ?? new TitleBlockInfo() },
            Identity(originalRootScreen)));
        refusals.Add(await RefusedIdentity(empty, "canonical", Identity(originalRootScreen.ToUpperInvariant())));
        refusals.Add(await RefusedIdentity(empty, "Atomic operation 1 rejected", Identity(originalRootScreen),
            new SchematicItemOperation { TargetDocument = document.Clone(), Update = Any.Pack(probeLabel) }));

        var reattach = await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = empty.State.Revision.Epoch });
        RequireToolSuccess(reattach);
        saved = store.Read()!;

        // Must-catch: XML edited after the files were lost is refused with nothing sent to KiCad; so is a design with a net
        // chain, whose rebuild is not proven yet. Planning alone, directly on the saved record.
        var renamed = originalDesign with { Engineering = originalDesign.Engineering with { Circuit = originalDesign.Engineering.Circuit with
            { Components = [.. originalDesign.Engineering.Circuit.Components.Select(c => c.Reference == "R1" ? c with { Reference = "R9" } : c)] } } };
        var unsettled = SchematicSynchronizationPlanner.Plan(saved.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(renamed, [])) }, token);
        Assert.AreEqual("rebuild_requires_settled_xml", unsettled.ErrorCode, unsettled.ErrorMessage);
        var chained = saved.State.Baseline with { Schematic = saved.State.Baseline.Schematic.Clone() };
        foreach (var screen in chained.Schematic.Instances) screen.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PSU_TO_CPU",
            From = new() { Reference = "U4", Pin = "8" }, To = new() { Reference = "U5", Pin = "74" }, MemberNets = { "/TELEM_MCU_TO_CPU" } });
        var withChains = SchematicSynchronizationPlanner.Plan(saved.State with { Baseline = chained,
            DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(chained, [])) }, token);
        Assert.AreEqual("rebuild_state_unrepresented", withChains.ErrorCode, withChains.ErrorMessage);

        var rebuildPlan = await Plan(saved, "rebuild-plan");
        Assert.IsTrue(rebuildPlan.GetProperty("nativeRebuildRequired").GetBoolean(), rebuildPlan.GetRawText());
        var rebuildOperations = Operations(rebuildPlan);
        Assert.AreEqual(originalRootScreen, rebuildOperations[0].RebuildScreenIdentity?.Value,
            "The rebuild first gives the new root the identity its file had.");
        Assert.AreEqual(expected.Symbols.Count, rebuildOperations.Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true));
        Assert.AreEqual(3, rebuildOperations.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        Assert.AreEqual(labelled.LocalLabels, rebuildOperations.Count(o => o.Create?.Is(LocalLabel.Descriptor) == true), "Every drawn label is rebuilt.");
        Assert.AreEqual(labelled.HierarchicalLabels, rebuildOperations.Count(o => o.Create?.Is(HierarchicalLabel.Descriptor) == true));
        Assert.AreEqual(labelled.Wires, rebuildOperations.Count(o => o.Create?.Is(SchematicLine.Descriptor) == true), "The root's wire is rebuilt.");
        CollectionAssert.AreEquivalent(originalSheetPins, rebuildOperations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true)
            .SelectMany(o => o.Create.Unpack<SheetSymbol>().Pins.Select(p => o.Create.Unpack<SheetSymbol>().Id.Value + "#" + p.Id.Value + "#" + p.Text.Text_)).ToArray(),
            "Each sheet symbol is rebuilt with its sheet pin and that pin's identity.");
        Assert.IsTrue(rebuildOperations.Select((o, i) => (o, i)).All(p => p.o.Create is not null || p.o.ReplaceLibraryCache is not null
            || SchematicRebuild.RecreatesFileState(p.o, p.i)), "The rebuild sends no project setting, removal or move.");
        Assert.AreEqual(empty, await Capture(), "Planning must not change KiCad.");
        var rebuild = await Apply(saved, "rebuild");

        // ---- 3. The rebuilt schematic is the original ----------------------------------------------------------
        var rebuilt = await Capture();
        Assert.IsFalse(rebuilt.State.NativeContentDirty, "Apply saved the rebuilt schematic.");
        Assert.AreEqual(original.State.StateSha256, rebuilt.State.StateSha256,
            "Every captured state group (each screen as KiCad saves it, and the project settings) equals the original.");
        Assert.AreEqual(original.State.SaveStableStateSha256, rebuilt.State.SaveStableStateSha256);
        var fileDifferences = new List<string>();
        foreach (var (file, bytes) in originalFiles)
        {
            byte[] now = await File.ReadAllBytesAsync(file, token);
            await File.WriteAllBytesAsync(Evidence("rebuilt-" + Path.GetFileName(file)), now, token);
            if (!now.AsSpan().SequenceEqual(bytes)) fileDifferences.Add(Path.GetFileName(file));
        }
        Assert.IsEmpty(fileDifferences, "Rebuilt files differ from the originals: " + string.Join(", ", fileDifferences));
        await File.WriteAllTextAsync(Evidence("rebuilt.xml"), SchematicDataXml.Write(rebuilt.Electrical.Hierarchy.Data), token);
        // Identities, sheets, objects and library caches: the same snapshot, apart from the loaded-file provenance of screens
        // KiCad has not loaded from their rebuilt files yet.
        var originalObjects = RebuildWithoutProvenance(original.Electrical.Hierarchy.Data);
        var rebuiltObjects = RebuildWithoutProvenance(rebuilt.Electrical.Hierarchy.Data);
        Assert.AreEqual(originalObjects.Instances.Count, rebuiltObjects.Instances.Count);
        foreach (var screen in originalObjects.Instances)
            Assert.AreEqual(screen, rebuiltObjects.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document)),
                "Rebuilt screen " + RebuildPathKey(screen) + " differs from the original.");
        Assert.AreEqual(original.Electrical.Nets.Count, rebuilt.Electrical.Nets.Count);
        CollectionAssert.AreEqual(RebuildPartition(original.Electrical), RebuildPartition(rebuilt.Electrical), "The pin partition is the original one.");
        var rebuiltDesign = store.Read()!.State.Baseline;
        var comparison = SchematicElectricalComparison.Compare(rebuiltDesign, rebuilt.Electrical, []);
        Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, "KiCad's rebuilt connections are exactly the XML nets.");
        var rebuiltReferences = rebuiltDesign.Engineering.Circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
        foreach (var (net, pins) in wired)
            Assert.IsTrue(comparison.PinPartitions!.Any(p => p.Pins.Select(x => rebuiltReferences[x.ComponentId] + "." + x.Pin).Order(StringComparer.Ordinal)
                .SequenceEqual(pins.Order(StringComparer.Ordinal))), net + " is one native net of exactly its two pins after the rebuild.");
        CollectionAssert.AreEquivalent(originalSheetPins, OriginalSheetPins(rebuilt.Electrical.Hierarchy.Data), "The sheet pins are the original ones.");
        Assert.AreEqual(SchematicDesignXml.Write(originalDesign with { Schematic = rebuiltDesign.Schematic }, []), SchematicDesignXml.Write(rebuiltDesign, []),
            "The published design is the original's engineering, bindings and part symbols.");

        // A second rebuild is a no-op: nothing to send, nothing to save, the XML unchanged.
        byte[] rebuiltXml = await File.ReadAllBytesAsync(path, token);
        var settled = await Plan(store.Read()!, "second-plan");
        Assert.IsFalse(settled.GetProperty("nativeRebuildRequired").GetBoolean(), settled.GetRawText());
        Assert.AreEqual(0, settled.GetProperty("nativeOperationsJson").GetArrayLength(), settled.GetRawText());
        var second = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath = path,
            expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
        await File.WriteAllTextAsync(Evidence("second-apply.json"), second.GetRawText(), token);
        RequireToolSuccess(second);
        var secondResult = second.GetProperty("structuredContent");
        Assert.IsFalse(secondResult.GetProperty("nativeMutationCommitted").GetBoolean(), second.GetRawText());
        Assert.IsFalse(secondResult.GetProperty("nativeFilesSaved").GetBoolean(), second.GetRawText());
        Assert.AreEqual(rebuilt, await Capture(), "A second rebuild changes nothing in KiCad.");
        CollectionAssert.AreEqual(rebuiltXml, await File.ReadAllBytesAsync(path, token), "A second rebuild leaves the XML untouched.");

        // One native undo takes the whole rebuild back to the empty root, identity included; redo restores it exactly.
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        var undone = await Until("native undo", s => s.Electrical.Hierarchy.Data.Instances.Count == 1);
        Assert.AreEqual(emptyRoot.Metadata.ScreenId.Value, undone.Electrical.Hierarchy.Data.Instances[0].Metadata.ScreenId.Value,
            "Undo returns the root to the identity KiCad gave it.");
        Assert.IsEmpty(undone.Electrical.Hierarchy.Data.Instances[0].Items);
        await FocusedSchematicShortcut(client, document, processId, display, "y", token);
        var redone = await Until("native redo", s => s.State.StateSha256 == rebuilt.State.StateSha256);
        Assert.AreEqual(originalRootScreen, redone.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.ScreenId.Value);

        await NativeKeyboard.CaptureAsync(display, Evidence("window.png"), token);
        await File.WriteAllTextAsync(Evidence("proof.json"), JsonSerializer.Serialize(new
        {
            instanceId, fixture = "psu-cpu", PsuCpuFixture.Version, stage = Stage.ToString(), seed = context.Seed.ToString(), realStdioProductionServer = true,
            steps, generatedSheets = generated.Select(g => new { g.SheetInstanceId, g.SheetSymbolId, g.ScreenId }),
            original = new { original.State.StateSha256, original.State.SaveStableStateSha256, screens = original.Electrical.Hierarchy.Data.Instances.Count,
                files = originalFiles.ToDictionary(f => Path.GetFileName(f.Key), f => Convert.ToHexStringLower(SHA256.HashData(f.Value))) },
            deleted = schematicFiles.Select(Path.GetFileName), newRootScreen = emptyRoot.Metadata.ScreenId.Value,
            rebuild = new { operations = rebuildOperations.Count, firstOperation = "rebuild_screen_identity", result = rebuild },
            rebuilt = new { rebuilt.State.StateSha256, sameStateDigest = true, filesByteIdentical = originalFiles.Count, sameObjects = true,
                samePinPartition = true, drawn = labelled, sheetPins = originalSheetPins, connections = wired.Select(w => new { w.Net, w.Pins }) },
            refused = new { unsettled = unsettled.ErrorCode, netChains = withChains.ErrorCode, nativeIdentity = refusals },
            secondApplyNoOp = true, undoRestoresEmptyRoot = true, redoRestoresRebuild = true, crossPlatformReady = false,
            remaining = "Complete stage (XML nets) needs lane 2A's connection realization; project-file reconstruction from XML; "
                + "rebuild of net chains and shared screens."
        }), token);

        async Task<StoredDesignRecovery> Desire(StoredDesignRecovery current, SchematicDesign design)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));
            await File.WriteAllBytesAsync(path, bytes, token);
            return store.Save(current.State with { DesiredFileBytes = bytes }, current.RevisionToken);
        }

        async Task<JsonElement> Plan(StoredDesignRecovery current, string name)
        {
            var plan = await host.Tool("kicad_design_sync_plan", Recovery(current.RevisionToken));
            await File.WriteAllTextAsync(Evidence(name + ".json"), RetainedToolEvidence(plan), token);
            RequireToolSuccess(plan);
            var content = plan.GetProperty("structuredContent");
            Assert.IsTrue(content.GetProperty("canPrepare").GetBoolean(), plan.GetRawText());
            return content;
        }

        // One label per pin, at its connection point and facing away from the pin body; for the crossing net, a sheet pin
        // on each of the PSU and CPU sheet symbols (on the edges that face each other) and a wire between them on the root.
        // All in one native edit.
        async Task<DrawnConnections> DrawConnections(CheckedSchematicState state, (int Sheet, string Net, string[] Pins, ConnectionLabelKind Kind)[] nets,
            string crossing)
        {
            var design = store.Read()!.State.Baseline;
            var policy = SchematicConnectionPolicy.FromSnapshot(state.Electrical.Hierarchy.Data);
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), Description = "Connect the VIN, MEM_SCL and " + crossing + " pins" };
            string SheetPath(int sheet) => string.Join('/', design.SheetBindings.Single(b => b.SheetInstanceId == PsuCpuIds.Id(0x05, sheet)).NativePath.Select(p => p.ToString("D")));
            foreach (var (sheet, net, pins, kind) in nets)
            {
                string nativePath = SheetPath(sheet);
                var screen = state.Electrical.Hierarchy.Data.Instances.Single(s => RebuildPathKey(s) == nativePath);
                var references = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .ToDictionary(s => s.Id.Value, s => s.ReferenceField.Text.Text_);
                var geometry = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                    { Document = screen.Metadata.Document.Clone(), ExpectedRevision = state.State.Revision.Clone() }, token);
                Assert.IsTrue(geometry.PinGeometryAvailable);
                foreach (string pin in pins)
                {
                    string reference = pin[..pin.IndexOf('.')], number = pin[(pin.IndexOf('.') + 1)..];
                    var anchor = geometry.Obstacles.Where(o => o.SymbolPins is not null && references.GetValueOrDefault(o.Id.Value) == reference)
                        .SelectMany(o => o.SymbolPins.Pins).Single(p => p.Number == number);
                    var spin = anchor.BodyDirectionX > 0 ? SchematicLabelSpinStyle.SlssLeft : anchor.BodyDirectionX < 0 ? SchematicLabelSpinStyle.SlssRight
                        : anchor.BodyDirectionY > 0 ? SchematicLabelSpinStyle.SlssUp : SchematicLabelSpinStyle.SlssBottom;
                    var label = SchematicConnectionRealizer.LabelPayload(kind, Guid.NewGuid(), anchor.Position, net, spin, policy);
                    batch.Operations.Add(new SchematicItemOperation { TargetDocument = screen.Metadata.Document.Clone(), Create = Any.Pack(label) });
                }
            }
            // The crossing on the root: PSU and CPU sit side by side in the row sheet generation placed them in.
            var root = state.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document));
            SheetSymbol Sheet(int sheet) => root.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
                .Single(x => x.Id.Value == SheetPath(sheet)[(SheetPath(sheet).LastIndexOf('/') + 1)..]);
            var psu = Sheet(2);
            var cpu = Sheet(3);
            Assert.AreEqual(psu.Position.YNm, cpu.Position.YNm, "Sheet generation placed PSU and CPU in one row.");
            Assert.IsLessThan(cpu.Position.XNm, psu.Position.XNm + psu.Size.XNm, "PSU is left of CPU.");
            long y = psu.Position.YNm + 2 * policy.SheetPinPitchNm;
            y = (y + policy.GridNm - 1) / policy.GridNm * policy.GridNm;
            Assert.IsLessThanOrEqualTo(psu.Position.YNm + psu.Size.YNm - policy.SheetPinPitchNm, y);
            var from = new Vector2 { XNm = psu.Position.XNm + psu.Size.XNm, YNm = y };
            var to = new Vector2 { XNm = cpu.Position.XNm, YNm = y };
            foreach (var (sheet, at, side) in new[] { (psu, from, SheetSide.ShsRight), (cpu, to, SheetSide.ShsLeft) })
            {
                var updated = sheet.Clone();
                updated.Pins.Add(new SheetPin
                {
                    Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Position = at.Clone(),
                    Text = new Text { Text_ = crossing, Attributes = new TextAttributes { Size = new Vector2 { XNm = policy.TextSizeNm, YNm = policy.TextSizeNm }, Multiline = false } },
                    SpinStyle = side == SheetSide.ShsLeft ? SchematicLabelSpinStyle.SlssRight : SchematicLabelSpinStyle.SlssLeft,
                    Shape = SchematicLabelShape.SlshPassive, Side = side, Locked = LockedState.LsUnlocked
                });
                batch.Operations.Add(new SchematicItemOperation { TargetDocument = document.Clone(), Update = Any.Pack(updated) });
            }
            batch.Operations.Add(new SchematicItemOperation { TargetDocument = document.Clone(), Create = Any.Pack(new SchematicLine
            {
                Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Start = from, End = to, Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked
            }) });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            return new(nets.Where(n => n.Kind == ConnectionLabelKind.Local).Sum(n => n.Pins.Length),
                nets.Where(n => n.Kind == ConnectionLabelKind.Hierarchical).Sum(n => n.Pins.Length), SheetPins: 2, Wires: 1);
        }

        // The identity a rebuild gives its new root, as its own operation on the project's root sheet.
        SchematicItemOperation Identity(string screen) => new() { TargetDocument = document.Clone(), RebuildScreenIdentity = new KIID { Value = screen } };

        // Send one checked batch at exactly this state, as apply does, and require KiCad to refuse it without any change:
        // a clean rejection with KiCad's reason, no native result, the same state and native identity before and after.
        async Task<object> RefusedIdentity(CheckedSchematicState at, string reason, params SchematicItemOperation[] operations)
        {
            var request = new CheckedSchematicBatch { ExpectedState = at.State.Clone(), Batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(), DocumentEpoch = at.State.Revision.Epoch, ExpectedRevision = at.State.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), Description = "Adopt a saved root identity (refusal probe)"
            } };
            request.Batch.Operations.Add(operations.Select(o => o.Clone()));
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
            string summary = receipt.Status + " " + receipt.ErrorCode + ": " + receipt.ErrorMessage;
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, receipt.Status, summary);
            Assert.AreEqual("native_batch_rejected", receipt.ErrorCode, summary);
            StringAssert.Contains(receipt.ErrorMessage, reason, summary);
            Assert.IsNull(receipt.Result, "A refused batch has no native result, so no identity change is reported.");
            Assert.AreEqual(receipt.ObservedBefore, receipt.ObservedAfter, summary);
            Assert.AreEqual(at.State.NativeIdentity, receipt.ObservedAfter.NativeIdentity, "The root keeps the identity it had.");
            Assert.AreEqual(at, await Capture(), "A refused identity changes nothing in KiCad.");
            return new { operations = operations.Select(o => o.OperationCase.ToString()), reason = receipt.ErrorMessage };
        }

        async Task<object> Apply(StoredDesignRecovery current, string name, bool nativeEdit = true)
        {
            var args = new { instanceId, recoveryPath = store.StatePath, designPath = path, expectedRevisionToken = current.RevisionToken,
                operationId = Guid.NewGuid().ToString("D") };
            var applied = await host.Tool("kicad_design_sync_apply", args);
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), applied.GetRawText(), token);
            if (applied.TryGetProperty("isError", out var failed) && failed.GetBoolean())
                await File.WriteAllTextAsync(Evidence(name + "-actual.xml"), SchematicDataXml.Write((await Capture()).Electrical.Hierarchy.Data), token);
            RequireToolSuccess(applied);
            var result = applied.GetProperty("structuredContent");
            Assert.AreEqual(nativeEdit, result.GetProperty("nativeMutationCommitted").GetBoolean(), applied.GetRawText());
            Assert.IsTrue(result.GetProperty("nativeFilesSaved").GetBoolean(), applied.GetRawText());
            Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), applied.GetRawText());
            var replay = await host.Tool("kicad_design_sync_apply", args);
            RequireToolSuccess(replay);
            Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(), "The same apply replays exactly.");
            Assert.IsFalse(store.Read()!.State.HasPendingWork);
            return new { name, published = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, token))),
                revision = (await Capture()).State.Revision.Sequence };
        }

        IReadOnlyList<SchematicItemOperation> Operations(JsonElement plan) => [.. plan.GetProperty("nativeOperationsJson").EnumerateArray()
            .Select(o => SchematicJson.Parser.Parse<SchematicItemOperation>(o.GetString()!))];

        async Task<CheckedSchematicState> Until(string what, Func<CheckedSchematicState, bool> reached)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                try
                {
                    var state = await Capture();
                    if (reached(state)) return state;
                }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                try { await Task.Delay(250, wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new AssertFailedException(what + " was not reached within 20 s.");
                }
            }
        }
    }

    private static string RebuildPathKey(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));

    private sealed record DrawnConnections(int LocalLabels, int HierarchicalLabels, int SheetPins, int Wires);

    // Every sheet pin KiCad shows, as "sheet symbol#sheet pin#text", with their exact identities.
    private static string[] OriginalSheetPins(SchematicHierarchyData data) => [.. data.Instances.SelectMany(s => s.Items)
        .Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
        .SelectMany(sheet => sheet.Pins.Select(p => sheet.Id.Value + "#" + p.Id.Value + "#" + p.Text.Text_)).Order(StringComparer.Ordinal)];

    private static SchematicHierarchyData RebuildWithoutProvenance(SchematicHierarchyData data)
    {
        var result = data.Clone();
        foreach (var screen in result.Instances) screen.Metadata.LoadedNativeFormatVersion = 0;
        return result;
    }

    // Each native net's placed pins as "path#pin", ordered: the exact pin partition without computed net names.
    private static string[] RebuildPartition(SchematicElectricalState state) => [.. state.Nets
        .Select(net => string.Join(" ", net.Sheets.SelectMany(s => s.Items.Select(i => string.Join('/', s.Path.Path.Select(p => p.Value)) + "#" + i.Value))
            .Order(StringComparer.Ordinal)))
        .Order(StringComparer.Ordinal)];
}
