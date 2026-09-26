using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9), lane 2C, ledgers p168f8654d86c5a1a,
    // pb985f81c60811999, p001c485926b37099 and p91fda8ca22a68141.
    //
    // A person's fully connected project whose schematic files are lost is rebuilt from its design XML, through the public MCP
    // tools of the production server over STDIO, with nothing lost. From the S2 seed (a root sheet only):
    //  1. The original project, built from XML. The fixture's four sheets are generated from XML that adds them; the XML then
    //     gives the CPU sheet an A3 page and arranges the sheet symbols as the fixture's S1 seed has them. The connection-aware
    //     layout tool places the whole Complete stage, its eight components are created from XML with those placements, and the
    //     Complete stage's eleven nets are drawn by lane 2A's connection realization (this KiCad advertises
    //     schematic.connection-realization.v1): labelled wire stubs on every pin, hierarchical labels, and sheet pins with
    //     their own stubs and labels on the parent sheets. KiCad's pin partition is exactly the Complete stage's.
    //     Before the creation and before the realization, the same apply is first made through the test host with a scripted
    //     refusal, so KiCad commits it and the follow-up check refuses it (ledgers p0aa59a1dfc8701ea, p6728215278183167; see
    //     NativeSynchronizationRecoveryJourney). Retrying, planning and reattaching cannot leave that state; the public tool
    //     kicad_design_recovery_resolve_pending can. The first project undoes the stuck creation and discards the stuck
    //     realization after KiCad's own undo, and the ordinary applies then resume; the second keeps both results and
    //     publishes them. The first project also kills a KiCad started for a copy of the realized project while an automatic
    //     worker watches it: the worker pauses with instance_exited within a second (pec2f1b53d4024a17). The second starts a
    //     KiCad for a copy too, forces a creation stuck there, joins two XML nets with a wire in KiCad, saves and reloads the
    //     sheets, and keeps KiCad's result: KiCad's connections are published and the nets' requirements wait for resolution.
    //  2. Records an earlier preview saved (p91fda8ca22a68141). Preview 23's snapshots listed library_cache among the state
    //     they could not hold; this build holds the library cache exactly and no longer lists it, so a preview 23 record sees
    //     a changed snapshot after upgrading. The same design's record, written as preview 23 wrote it (the only difference
    //     is that list), reattaches and plans normally: the planned design is the one this build writes and nothing is sent
    //     to KiCad, and applying succeeds with nothing to send, save or write (before, it failed as a settings change). The
    //     design file keeps preview 23's list until a synchronization next publishes the design. A record saved before
    //     electrical checkpoints stops at planning with the one action that fixes it, kicad_design_electrical_baseline_initialize,
    //     which then succeeds (before, it failed with a spurious electrical_baseline_mismatch), and the record plans and
    //     applies normally. These records are this design's, written in preview 23's format by this build's store; a record
    //     preview 23 itself wrote is kept byte for byte in automation/tests/fixtures/preview-23-recovery and read, planned
    //     and saved again by DesignElectricalRecoveryTests.ARecordPreview23WroteReadsAndPlansAgainstThisBuildsSnapshot.
    //  3. Every schematic file is lost, KiCad creates a new empty root for the project and the recovery record adopts it.
    //     Two ways the kept project settings can differ from the XML are refused through the public tools, each with the
    //     project file, the XML and KiCad unchanged (p001c485926b37099). The kept project file changed on disk (a text
    //     variable added): KiCad reads a project file only when it opens the project, so its new root still shows the
    //     settings it loaded, the plan is the rebuild, and apply refuses it with native_file_conflict before anything reaches
    //     KiCad, so the changed file is never overwritten. KiCad's own project settings changed (the same text variable
    //     added with kicad_schematic_apply_checked_batch): planning and applying are refused with
    //     rebuild_project_settings_changed and a message naming the settings KiCad shows and the actions that work while
    //     the files are lost (change them back in KiCad, or restore the saved project file and reopen the project). The
    //     file is restored, and the journey takes the first action, after which the rebuild proceeds.
    //     (The harness KiCad cannot be started again inside the journey: NativeSessionTests checks afterwards that it kept its
    //     process epoch and still holds the project. Planning on a KiCad started after the file changed is the same
    //     classification, proven with every setting group in SchematicRebuildTests.)
    //  4. Apply rebuilds the schematic from the XML last synchronized with KiCad (pb985f81c60811999). The rebuilt schematic
    //     is the original on every captured state group: the whole-document state digest and each saved file byte for byte,
    //     with the same identities, sheets, library caches, sheet pins, labels and wires, and the Complete pin partition. A
    //     second apply is a no-op, and one native undo returns to the empty root while redo restores the rebuild.
    // The native rules of the root identity a rebuild adopts are exercised here too, in the live editor: a root KiCad loaded
    // from its file, an identity that is not first or not canonical, and a batch that fails after the identity (which must
    // leave the root's own identity) are refused with nothing changed.
    // The earlier journey also drew three nets by hand in KiCad and published them to the XML before the files were lost.
    // They are left out because the coverage they would add was judged marginal, not because they would disturb the
    // Complete partition (a redundant connection drawn inside an existing net would not). A rebuild recreates every object
    // of the XML last synchronized with KiCad, whatever path brought it there (PlanRebuild reads only that XML), and every
    // kind of object those nets drew is drawn here by the realization and rebuilt: local and hierarchical labels with the
    // same label payload (SchematicConnectionRealizer.LabelPayload), sheet pins and wires.
    private static async Task VerifyPsuCpuXmlRebuild(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        const PsuCpuStage Stage = PsuCpuStage.Complete;
        const long LayoutClearanceNm = 7_620_000L;
        var expected = PsuCpuFixture.ExpectedNative(Stage);
        string path = context.DesignPath;
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-xml-rebuild-" + name);
        var store = new DesignRecoveryStore(Evidence("recovery.json"));
        var document = context.Root;
        Assert.AreEqual(PsuCpuSeed.RootOnly, context.Seed);
        // The harness runs this journey for two projects, in folders 0 and 1. Operations KiCad applied but whose check refused
        // them are undone or discarded in the first and kept in the second (NativeSynchronizationRecoveryJourney).
        bool keeps = Path.GetFileName(Path.TrimEndingDirectorySeparator(context.ProjectDirectory)) == "1";
        var clock = Stopwatch.StartNew();
        var steps = new List<object>();
        void Step(string name, object? detail = null)
        {
            steps.Add(new { name, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), detail });
            Console.WriteLine($"PSU/CPU XML rebuild {instanceId}: {name} at {clock.Elapsed.TotalSeconds:F1}s");
        }
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        string projectFile = Path.Combine(context.ProjectDirectory, "fixture.kicad_pro");
        var schematicFiles = new[] { "fixture.kicad_sch", "psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" }
            .Select(name => Path.Combine(context.ProjectDirectory, name)).ToArray();

        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(), Evidence("host"), Evidence("host.log"), token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        object Recovery(DesignRecoveryStore on) => new { instanceId, recoveryPath = on.StatePath, expectedRevisionToken = on.Read()!.RevisionToken };
        // The earlier previews' records hold the whole design like the journey's own; they live outside the retained evidence.
        string earlierRecords = Directory.CreateTempSubdirectory("kicad-earlier-preview-records-").FullName;
        try
        {
            // ---- 1. The original project, built from XML ------------------------------------------------------
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
            var generationPlan = await Plan(store, "generation-plan");
            Assert.IsTrue(generationPlan.GetProperty("nativeRebuildRequired").GetBoolean(), generationPlan.GetRawText());
            var generated = new[] { 2, 3, 4 }.Select(n => SchematicRebuild.GeneratedSheet(PsuCpuIds.Id(0x05, n))).ToArray();
            CollectionAssert.AreEqual(generated.Select(g => g.SheetInstanceId.ToString("D")).ToArray(),
                generationPlan.GetProperty("rebuildSheetInstances").EnumerateArray().Select(e => e.GetString()).ToArray());
            var generationOperations = Operations(generationPlan);
            Assert.AreEqual(3, generationOperations.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true), "One sheet symbol per new sheet.");
            Assert.AreEqual(seed, await Capture(), "Planning must not change KiCad.");
            await Apply(store, "generation");
            var generatedDesign = store.Read()!.State.Baseline;
            var generatedNative = await Capture();
            Assert.HasCount(4, generatedNative.Electrical.Hierarchy.Data.Instances, "KiCad shows the root and the three generated sheets.");
            foreach (var (identity, sheet) in generated.Zip(expected.Sheets.Where(s => s.Parent is not null)))
            {
                var binding = generatedDesign.SheetBindings.Single(b => b.SheetInstanceId == identity.SheetInstanceId);
                var screen = generatedNative.Electrical.Hierarchy.Data.Instances.Single(s => RebuildPathKey(s) == string.Join('/', binding.NativePath.Select(p => p.ToString("D"))));
                Assert.AreEqual(identity.SheetSymbolId, binding.NativePath[^1], sheet.Key + " is bound to its generated sheet symbol.");
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
            Step("sheets generated", new { operations = generationOperations.Count });

            // 1b. The CPU sheet holds three units of a 177-pin processor, so the XML gives it an A3 page (contract §1.6.2), and
            // the sheet symbols are arranged as the fixture's S1 seed has them, so every sheet crossing has room for its pins,
            // stubs and labels: PSU and CPU side by side on the root, CPU_POWER at the top right of the A3 CPU sheet.
            var arranged = generatedDesign with { Schematic = generatedDesign.Schematic.Clone() };
            string SheetPathOf(SchematicDesign design, int sheet) =>
                string.Join('/', design.SheetBindings.Single(b => b.SheetInstanceId == PsuCpuIds.Id(0x05, sheet)).NativePath.Select(p => p.ToString("D")));
            var rootScreen = arranged.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document));
            var cpuScreen = arranged.Schematic.Instances.Single(s => RebuildPathKey(s) == SheetPathOf(arranged, 3));
            cpuScreen.Metadata.Page.PageSize = PageSize.PsA3;
            ArrangeSheet(rootScreen, generated[0].SheetSymbolId, 50_800_000, 50_800_000, 50_800_000);
            ArrangeSheet(rootScreen, generated[1].SheetSymbolId, 152_400_000, 50_800_000, 50_800_000);
            ArrangeSheet(cpuScreen, generated[2].SheetSymbolId, 330_200_000, 25_400_000, 25_400_000);
            saved = await Desire(store.Read()!, arranged);
            var arrangementPlan = await Plan(store, "arrangement-plan");
            Assert.IsFalse(arrangementPlan.GetProperty("nativeRebuildRequired").GetBoolean(), "A page and sheet arrangement is an ordinary synchronization.");
            Assert.IsFalse(Operations(arrangementPlan).Any(o => o.Create is not null), "Arranging creates nothing.");
            await Apply(store, "arrangement");
            var arrangedNative = await Capture();
            foreach (var (screenPath, sheetId) in new[] { (SheetPathOf(arranged, 1), generated[0].SheetSymbolId), (SheetPathOf(arranged, 1), generated[1].SheetSymbolId),
                (SheetPathOf(arranged, 3), generated[2].SheetSymbolId) })
            {
                SheetSymbol Find(SchematicHierarchyData data) => data.Instances.Single(s => RebuildPathKey(s) == screenPath).Items
                    .Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Value == sheetId.ToString("D"));
                var wanted = Find(arranged.Schematic);
                var shown = Find(arrangedNative.Electrical.Hierarchy.Data);
                Assert.AreEqual((wanted.Position, wanted.Size), (shown.Position, shown.Size), "KiCad shows the arranged sheet symbol " + wanted.NameField.Text.Text_);
            }
            Assert.AreEqual(PageSize.PsA3, arrangedNative.Electrical.Hierarchy.Data.Instances.Single(s => RebuildPathKey(s) == SheetPathOf(arranged, 3)).Metadata.Page.PageSize);
            Step("sheets arranged");

            // 1c. The connection-aware layout of the whole Complete stage (CN-1 §10), on the page each sheet has in KiCad. The
            // layout keeps room beside each pin for the shortest wire stub (two grid steps) and its label, while the realization
            // draws up to eight grid steps where labels on neighbouring pins must be staggered. With 2.54 mm between symbols, the
            // order the generated sheets give (the memory right of the processor's 98-pin unit) left U6.2's GND label on the
            // processor's pins (realization_no_free_stub, run t20260925T114807Z-ccb46a). So the symbols keep 7.62 mm apart:
            // the difference between the longest and the shortest stub.
            var published = store.Read()!.State.Baseline;
            var complete = PsuCpuFixture.Engineering(Stage);
            Assert.HasCount(11, complete.Circuit.Nets);
            Assert.IsTrue(complete.Circuit.Symbols.All(s => s.Placement is null), "The frozen fixture occurrences are coordinate-free.");
            saved = await Desire(store.Read()!, published with { Engineering = complete, PartSymbols = [.. context.PartSymbols] });
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
                expectedRevisionToken = saved.RevisionToken, gridNm = 1_270_000L, clearanceNm = LayoutClearanceNm, pageInsetNm = 0L, regions,
                userInstructions = "Lay out the PSU/CPU circuit on its sheets with room for every connection, and keep the processor's power unit on CPU_POWER." });
            await File.WriteAllTextAsync(Evidence("layout.json"), RetainedToolEvidence(layout), token);
            RequireToolSuccess(layout);
            Assert.IsTrue(layout.GetProperty("structuredContent").GetProperty("canPropose").GetBoolean(), layout.GetRawText());
            var laidOut = SchematicDesignXml.Read(layout.GetProperty("structuredContent").GetProperty("desiredXml").GetString()!, []);
            Assert.AreEqual(CircuitXml.Write(complete.Circuit), CircuitXml.Write(laidOut.Engineering.Circuit.WithoutPlacement()), "Layout only adds coordinates.");
            var placements = laidOut.Engineering.Circuit.Symbols.ToDictionary(s => s.Id, s => s.Placement ?? throw new AssertFailedException("Every unit is placed."));
            Assert.AreEqual(measured, await Capture(), "Layout must not change KiCad.");
            Step("complete stage laid out");

            // 1d. The eight components on their sheets with those placements (the Components stage).
            var components = PsuCpuFixture.Engineering(PsuCpuStage.Components);
            components = components with { Circuit = components.Circuit with
                { Symbols = [.. components.Circuit.Symbols.Select(s => s with { Placement = placements[s.Id] })] } };
            saved = await Desire(store.Read()!, published with { Engineering = components, PartSymbols = [.. context.PartSymbols] });
            var creationPlan = await Plan(store, "creation-plan");
            Assert.IsFalse(creationPlan.GetProperty("nativeRebuildRequired").GetBoolean(), "Component creation is lane 2A's creation path.");
            Assert.AreEqual(expected.Symbols.Count, Operations(creationPlan).Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true));
            // A creation that KiCad applied but whose connection check refused it (NativeSynchronizationRecoveryJourney): the
            // first project undoes it and the ordinary apply creates the components again, the second keeps KiCad's result.
            var stuckCreation = await ForceStuckSynchronization(client, document, store, path, instanceId, "creation-check",
                "native_sync_connectivity_mismatch", evidence, token);
            Step("creation stuck after KiCad applied it", stuckCreation.Evidence);
            object creationExit;
            if (keeps) creationExit = await KeepStuckSynchronization(host, client, document, store, stuckCreation, instanceId, evidence, token);
            else
            {
                creationExit = await UndoStuckSynchronization(host, client, document, store, stuckCreation, instanceId, evidence, token);
                await Apply(store, "creation");
            }
            PsuCpuFixture.AssertNative(store.Read()!.State.Baseline, (await Capture()).Electrical, PsuCpuStage.Components);
            Step(keeps ? "stuck creation kept and published" : "stuck creation undone, components created", creationExit);

            // 1e. The Complete stage: the created design with the fixture's eleven nets, drawn by the connection realization.
            var created = store.Read()!.State.Baseline;
            saved = await Desire(store.Read()!, created with { Engineering = created.Engineering with
                { Circuit = created.Engineering.Circuit with { Nets = complete.Circuit.Nets } } });
            var beforeRealization = await Capture();
            var realizationPlan = await Plan(store, "realization-plan");
            Assert.IsTrue(realizationPlan.GetProperty("connectionRealizationRequired").GetBoolean(), realizationPlan.GetRawText());
            Assert.IsFalse(realizationPlan.GetProperty("nativeRebuildRequired").GetBoolean(), "Drawing XML nets is the connection realization, not a rebuild.");
            Assert.AreEqual(11, realizationPlan.GetProperty("connectionIntent").GetProperty("nets").GetArrayLength(), "The preview names every fixture net.");
            Assert.AreEqual(beforeRealization, await Capture(), "Planning must not change KiCad.");
            // A realization that KiCad committed but whose resolution refused it: the first project takes it back in KiCad and
            // discards it, and the ordinary apply realizes the nets again; the second keeps KiCad's drawing.
            var stuckRealization = await ForceStuckSynchronization(client, document, store, path, instanceId, "realization-resolution",
                SchematicConnectionErrors.RealizationResolutionMismatch, evidence, token);
            Assert.IsTrue(stuckRealization.NativeReceipt.Result.ConnectivityAssertionVerified,
                "KiCad verified the connectivity assertion of the stuck realization; only the scripted check refused it.");
            Step("realization stuck after KiCad committed it", stuckRealization.Evidence);
            object realizationExit;
            if (keeps)
                realizationExit = await KeepStuckSynchronization(host, client, document, store, stuckRealization, instanceId, evidence, token);
            else
            {
                realizationExit = await DiscardStuckSynchronization(host, client, document, store, stuckRealization, processId, display, instanceId, evidence, token);
                var realization = await Apply(store, "realization");
                Assert.IsTrue(realization.GetProperty("nativeReceipt").GetProperty("result").GetProperty("connectivityAssertionVerified").GetBoolean(),
                    "KiCad verified the connectivity assertion of the realization: " + realization.GetRawText());
            }
            var realized = await Capture();
            Assert.IsFalse(realized.State.NativeContentDirty, "Apply saved the realized sheets.");
            PsuCpuFixture.AssertNative(store.Read()!.State.Baseline, realized.Electrical, Stage);
            var drawn = DrawnObjects(realized.Electrical.Hierarchy.Data);
            Assert.IsTrue(drawn["LocalLabel"] > 0 && drawn["HierarchicalLabel"] > 0 && drawn["SchematicLine"] > 0 && drawn["SheetPin"] > 0,
                "The realization drew labels, hierarchical labels, wires and sheet pins: " + JsonSerializer.Serialize(drawn));
            Step("complete stage realized", new { drawn, realizationExit });
            // An automatic worker whose KiCad is killed pauses at once with instance_exited (first project). A creation kept after
            // KiCad joined two XML nets and reloaded its saved sheets publishes KiCad's connections (second project).
            object? exitPause = keeps ? null : await VerifyAutomaticPauseOnExit(host, store.Read()!.State.Baseline, context.ProjectDirectory, display,
                evidence, instanceId, token);
            if (exitPause is not null) Step("automatic worker paused on its KiCad's exit", exitPause);
            object? keptConnections = keeps ? await VerifyKeptConnectionsWin(host, store.Read()!.State.Baseline, context.ProjectDirectory, display,
                evidence, instanceId, token) : null;
            if (keptConnections is not null) Step("KiCad's own connections kept and published", keptConnections);

            // ---- 2. Records an earlier preview saved (p91fda8ca22a68141) ---------------------------------------
            var settledRecord = store.Read()!;
            byte[] currentXml = await File.ReadAllBytesAsync(path, token);
            string[] coverage = [SchematicRebuild.RetainedProjectSettings, "shared_screen_root_ownership", "net_chains"];
            // KiCad names what no snapshot holds completely; this schematic has no sheet file shown twice and no net chain, so
            // only the untyped project settings are not in the XML, and they stay in the kept project file.
            foreach (var screen in realized.Electrical.Hierarchy.Data.Instances)
            {
                CollectionAssert.AreEqual(coverage, screen.Metadata.UnrepresentedState.ToArray(), "The snapshot's coverage list is the same for every schematic.");
                Assert.IsEmpty(screen.Metadata.NetChains);
            }
            Assert.IsFalse(SchematicRebuild.Lost("shared_screen_root_ownership", realized.Electrical.Hierarchy.Data));
            var earlier = await EarlierPreviewRecords(settledRecord, currentXml, realized);
            Step("earlier preview records plan normally", earlier);

            // The original: saved by KiCad, clean, and the XML settled on it.
            var original = await Capture();
            Assert.AreEqual(realized, original, "Adopting the earlier records sent nothing to KiCad.");
            var originalDesign = store.Read()!.State.Baseline;
            var originalSheetPins = OriginalSheetPins(original.Electrical.Hierarchy.Data);
            Assert.HasCount(12, originalSheetPins, "PSU and CPU each hold the five crossing nets' sheet pins, CPU_POWER two (contract §1.6.3).");
            var originalFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string file in schematicFiles.Append(projectFile))
            {
                originalFiles[file] = await File.ReadAllBytesAsync(file, token);
                await File.WriteAllBytesAsync(Evidence("original-" + Path.GetFileName(file)), originalFiles[file], token);
            }
            byte[] originalXml = await File.ReadAllBytesAsync(path, token);
            CollectionAssert.AreEqual(currentXml, originalXml);
            await File.WriteAllTextAsync(Evidence("original.xml"), SchematicDataXml.Write(original.Electrical.Hierarchy.Data), token);
            string originalRootScreen = original.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.ScreenId.Value;

            // ---- 3. The files are lost; changed project settings are refused -------------------------------------
            var close = await host.Tool("kicad_document_close", new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(original.State),
                operationId = Guid.NewGuid().ToString("D") });
            await File.WriteAllTextAsync(Evidence("close.json"), close.GetRawText(), token);
            RequireToolSuccess(close);
            foreach (string file in schematicFiles) File.Delete(file);
            Assert.IsFalse(schematicFiles.Any(File.Exists), "Every schematic file is gone.");
            var changedProject = JsonNode.Parse(originalFiles[projectFile])!.AsObject();
            if (changedProject["text_variables"] is not JsonObject variables) changedProject["text_variables"] = variables = new JsonObject();
            variables["FIXTURE_REVISION"] = "B";
            byte[] changedProjectBytes = Encoding.UTF8.GetBytes(changedProject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            await File.WriteAllBytesAsync(projectFile, changedProjectBytes, token);
            var empty = await CreateRoot();
            var emptyRoot = empty.Electrical.Hierarchy.Data.Instances.Single();
            Assert.IsFalse(emptyRoot.Metadata.TextVariables.ContainsKey("FIXTURE_REVISION"), "KiCad's new root shows the settings KiCad loaded with the project.");
            Assert.AreNotEqual(original.State.Revision.Epoch, empty.State.Revision.Epoch, "The new root is a new document session.");
            Assert.AreNotEqual(originalRootScreen, emptyRoot.Metadata.ScreenId.Value, "KiCad gives the new root a new screen identity.");
            CollectionAssert.AreEqual(originalXml, await File.ReadAllBytesAsync(path, token), "Losing the schematic files leaves the XML untouched.");
            RequireToolSuccess(await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = empty.State.Revision.Epoch }));
            // (a) The kept project file changed on disk: the rebuild is planned, and apply refuses to overwrite the file.
            var changedFilePlan = await Plan(store, "changed-project-file-plan");
            Assert.IsTrue(changedFilePlan.GetProperty("nativeRebuildRequired").GetBoolean(), "Planning reads no file; KiCad shows the settings the XML records.");
            var changedFile = await RefusedRebuild("changed-project-file", changedProjectBytes, empty);
            Assert.AreEqual("native_file_conflict", changedFile.Code, changedFile.Message);
            Assert.AreEqual("Native file baselines must be known and unchanged before synchronization.", changedFile.Message);
            await File.WriteAllBytesAsync(projectFile, originalFiles[projectFile], token);
            var fileRestored = await Capture();
            Assert.IsTrue(CheckedSchematicContract.FileCoverage(fileRestored.State), "The restored project file is the one KiCad loaded.");
            Step("changed project file refused");
            // (b) KiCad's own project settings changed on the new root: planning and applying are refused.
            var withVariable = emptyRoot.Metadata.TextVariables.ToDictionary(v => v.Key, v => v.Value);
            withVariable["FIXTURE_REVISION"] = "B";
            var changedSettings = await EditTextVariables(fileRestored, withVariable, "Add a text variable to the project (refusal probe)");
            Assert.AreEqual("B", changedSettings.Electrical.Hierarchy.Data.Instances[0].Metadata.TextVariables.GetValueOrDefault("FIXTURE_REVISION"));
            RequireToolSuccess(await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = changedSettings.State.Revision.Epoch }));
            const string SettingsRefusal = "KiCad's project settings (text variables) differ from the ones the XML records, so rebuilding would "
                + "overwrite them. Put them back as the XML records them (change them back in KiCad, or restore the project file KiCad last "
                + "saved with this XML and reopen the project), then rebuild.";
            var settingsPlan = await host.Tool("kicad_design_sync_plan", Recovery(store));
            await File.WriteAllTextAsync(Evidence("changed-settings-plan.json"), RetainedToolEvidence(settingsPlan), token);
            Assert.AreEqual("rebuild_project_settings_changed", Error(settingsPlan), settingsPlan.GetRawText());
            var settingsContent = settingsPlan.GetProperty("structuredContent");
            Assert.AreEqual(SettingsRefusal, settingsContent.GetProperty("errorMessage").GetString());
            Assert.IsFalse(settingsContent.GetProperty("canPrepare").GetBoolean());
            Assert.AreEqual(0, settingsContent.GetProperty("nativeOperationsJson").GetArrayLength(), "Nothing would reach KiCad.");
            Assert.AreEqual(JsonValueKind.Null, settingsContent.GetProperty("candidateDesignXml").ValueKind, "Nothing would be published.");
            var changedSetting = await RefusedRebuild("changed-settings", originalFiles[projectFile], changedSettings);
            Assert.AreEqual("rebuild_project_settings_changed", changedSetting.Code, changedSetting.Message);
            Assert.AreEqual(SettingsRefusal, changedSetting.Message);
            // The refusal's first action: the setting is changed back in KiCad, and the record adopts the root as KiCad shows it again.
            var restored = await EditTextVariables(changedSettings, emptyRoot.Metadata.TextVariables.ToDictionary(v => v.Key, v => v.Value),
                "Restore the project's text variables");
            Assert.IsTrue(emptyRoot.Metadata.TextVariables.Equals(restored.Electrical.Hierarchy.Data.Instances[0].Metadata.TextVariables));
            Assert.IsEmpty(restored.Electrical.Hierarchy.Data.Instances[0].Items);
            Step("changed project settings refused");

            // ---- 4. The schematic is rebuilt from the XML -------------------------------------------------------
            // Must-catch, in the live editor through the checked batch path apply uses: the identity is only ever a batch's
            // first operation and one canonical UUID, and a batch that fails after it leaves the root the identity KiCad gave it.
            var probeLabel = new LocalLabel { Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Position = new Vector2(), Text = new Text { Text_ = "PROBE" } };
            refusals.Add(await RefusedIdentity(restored, "must be the first operation",
                new SchematicItemOperation { TargetDocument = document.Clone(), SetTitleBlock = emptyRoot.Metadata.TitleBlock?.Clone() ?? new TitleBlockInfo() },
                Identity(originalRootScreen)));
            refusals.Add(await RefusedIdentity(restored, "canonical", Identity(originalRootScreen.ToUpperInvariant())));
            refusals.Add(await RefusedIdentity(restored, "Atomic operation 1 rejected", Identity(originalRootScreen),
                new SchematicItemOperation { TargetDocument = document.Clone(), Update = Any.Pack(probeLabel) }));

            RequireToolSuccess(await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = restored.State.Revision.Epoch }));
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

            var rebuildPlan = await Plan(store, "rebuild-plan");
            Assert.IsTrue(rebuildPlan.GetProperty("nativeRebuildRequired").GetBoolean(), rebuildPlan.GetRawText());
            Assert.IsFalse(rebuildPlan.GetProperty("connectionRealizationRequired").GetBoolean(), "The rebuild recreates the drawn connections; it realizes nothing new.");
            var rebuildOperations = Operations(rebuildPlan);
            Assert.AreEqual(originalRootScreen, rebuildOperations[0].RebuildScreenIdentity?.Value, "The rebuild first gives the new root the identity its file had.");
            Guid[] originalIds = [.. original.Electrical.Hierarchy.Data.Instances.SelectMany(s => SchematicItemDelta.Index(s.Items).Keys).Order()];
            Guid[] createdIds = [.. rebuildOperations.Where(o => o.Create is not null).SelectMany(o => SchematicItemDelta.Index([o.Create]).Keys).Order()];
            CollectionAssert.AreEqual(originalIds, createdIds, "The rebuild creates every object of the original with its identity, and nothing else.");
            var rebuiltKinds = rebuildOperations.Where(o => o.Create is not null).GroupBy(o => Any.GetTypeName(o.Create.TypeUrl).Split('.')[^1])
                .ToDictionary(g => g.Key, g => g.Count());
            Assert.AreEqual(expected.Symbols.Count, rebuiltKinds.GetValueOrDefault("SchematicSymbolInstance"));
            Assert.AreEqual(3, rebuiltKinds.GetValueOrDefault("SheetSymbol"));
            foreach (string kind in new[] { "LocalLabel", "HierarchicalLabel", "SchematicLine" })
                Assert.AreEqual(drawn[kind], rebuiltKinds.GetValueOrDefault(kind), "Every drawn " + kind + " is rebuilt.");
            CollectionAssert.AreEquivalent(originalSheetPins, rebuildOperations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true)
                .SelectMany(o => o.Create.Unpack<SheetSymbol>().Pins.Select(p => o.Create.Unpack<SheetSymbol>().Id.Value + "#" + p.Id.Value + "#" + p.Text.Text_)).ToArray(),
                "Each sheet symbol is rebuilt with its sheet pins and their identities.");
            Assert.IsTrue(rebuildOperations.Select((o, i) => (o, i)).All(p => p.o.Create is not null || p.o.ReplaceLibraryCache is not null
                || SchematicRebuild.RecreatesFileState(p.o, p.i)), "The rebuild sends no project setting, removal or move.");
            Assert.AreEqual(restored, await Capture(), "Planning must not change KiCad.");
            var rebuild = await Apply(store, "rebuild");
            Step("rebuilt", rebuiltKinds);

            // The rebuilt schematic is the original.
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
            // Identities, sheets, objects and library caches: the same snapshot, apart from the loaded-file provenance of screens
            // KiCad has not loaded from their rebuilt files yet.
            var originalObjects = RebuildWithoutProvenance(original.Electrical.Hierarchy.Data);
            var rebuiltObjects = RebuildWithoutProvenance(rebuilt.Electrical.Hierarchy.Data);
            var screenDifferences = originalObjects.Instances.Where(screen => !Equals(screen,
                rebuiltObjects.Instances.SingleOrDefault(s => s.Metadata.Document.Equals(screen.Metadata.Document)))).Select(RebuildPathKey).ToArray();
            if (fileDifferences.Count != 0 || screenDifferences.Length != 0)
                await File.WriteAllTextAsync(Evidence("rebuilt.xml"), SchematicDataXml.Write(rebuilt.Electrical.Hierarchy.Data), token);
            Assert.IsEmpty(fileDifferences, "Rebuilt files differ from the originals: " + string.Join(", ", fileDifferences));
            Assert.AreEqual(originalObjects.Instances.Count, rebuiltObjects.Instances.Count);
            Assert.IsEmpty(screenDifferences, "Rebuilt screens differ from the original: " + string.Join(", ", screenDifferences));
            Assert.AreEqual(original.Electrical.Nets.Count, rebuilt.Electrical.Nets.Count);
            CollectionAssert.AreEqual(RebuildPartition(original.Electrical), RebuildPartition(rebuilt.Electrical), "The pin partition is the original one.");
            var rebuiltDesign = store.Read()!.State.Baseline;
            PsuCpuFixture.AssertNative(rebuiltDesign, rebuilt.Electrical, Stage);
            var comparison = SchematicElectricalComparison.Compare(rebuiltDesign, rebuilt.Electrical, []);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, "KiCad's rebuilt connections are exactly the XML nets.");
            CollectionAssert.AreEquivalent(originalSheetPins, OriginalSheetPins(rebuilt.Electrical.Hierarchy.Data), "The sheet pins are the original ones.");
            Assert.AreEqual(SchematicDesignXml.Write(originalDesign with { Schematic = rebuiltDesign.Schematic }, []), SchematicDesignXml.Write(rebuiltDesign, []),
                "The published design is the original's engineering, bindings and part symbols.");

            // A second rebuild is a no-op: nothing to send, nothing to save, the XML unchanged.
            byte[] rebuiltXml = await File.ReadAllBytesAsync(path, token);
            var settledPlan = await Plan(store, "second-plan");
            Assert.IsFalse(settledPlan.GetProperty("nativeRebuildRequired").GetBoolean(), settledPlan.GetRawText());
            Assert.AreEqual(0, settledPlan.GetProperty("nativeOperationsJson").GetArrayLength(), settledPlan.GetRawText());
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
            Step("second apply no-op, undo and redo");

            await NativeKeyboard.CaptureAsync(display, Evidence("window.png"), token);
            await File.WriteAllTextAsync(Evidence("proof.json"), JsonSerializer.Serialize(new
            {
                instanceId, fixture = "psu-cpu", PsuCpuFixture.Version, stage = Stage.ToString(), seed = context.Seed.ToString(), realStdioProductionServer = true,
                steps, layoutClearanceNm = LayoutClearanceNm,
                stuckOperations = new { project = keeps ? "keep-and-replan, and KiCad's own connections kept" : "undo, discard and exit pause",
                    creation = creationExit, realization = realizationExit, exitPause, keptConnections }, generatedSheets = generated.Select(g => new { g.SheetInstanceId, g.SheetSymbolId, g.ScreenId }),
                original = new { original.State.StateSha256, original.State.SaveStableStateSha256, screens = original.Electrical.Hierarchy.Data.Instances.Count,
                    drawn, sheetPins = originalSheetPins.Length, nets = original.Electrical.Nets.Count,
                    files = originalFiles.ToDictionary(f => Path.GetFileName(f.Key), f => Convert.ToHexStringLower(SHA256.HashData(f.Value))) },
                earlierPreviewRecords = earlier,
                changedProjectFile = new { planIsRebuild = true, applyRefused = changedFile.Code, message = changedFile.Message,
                    projectFileUnchanged = true, kicadUnchanged = true, xmlUnchanged = true },
                changedProjectSettings = new { planRefused = "rebuild_project_settings_changed", applyRefused = changedSetting.Code, message = SettingsRefusal,
                    projectFileUnchanged = true, kicadUnchanged = true, xmlUnchanged = true },
                deleted = schematicFiles.Select(Path.GetFileName), newRootScreen = emptyRoot.Metadata.ScreenId.Value,
                rebuild = new { operations = rebuildOperations.Count, created = rebuiltKinds, firstOperation = "rebuild_screen_identity", result = rebuild },
                rebuilt = new { rebuilt.State.StateSha256, sameStateDigest = true, filesByteIdentical = originalFiles.Count, sameObjects = true,
                    samePinPartition = true, completePartition = true },
                refused = new { unsettled = unsettled.ErrorCode, netChains = withChains.ErrorCode, nativeIdentity = refusals },
                secondApplyNoOp = true, undoRestoresEmptyRoot = true, redoRestoresRebuild = true, crossPlatformReady = false,
                remaining = "Project-file reconstruction from XML; rebuild of net chains and shared screens."
            }), token);
            Step("done");
        }
        finally
        {
            try { Directory.Delete(earlierRecords, true); } catch (IOException) { }
        }

        async Task<StoredDesignRecovery> Desire(StoredDesignRecovery current, SchematicDesign design)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));
            await File.WriteAllBytesAsync(path, bytes, token);
            return store.Save(current.State with { DesiredFileBytes = bytes }, current.RevisionToken);
        }

        async Task<JsonElement> Plan(DesignRecoveryStore on, string name)
        {
            var plan = await host.Tool("kicad_design_sync_plan", Recovery(on));
            await File.WriteAllTextAsync(Evidence(name + ".json"), RetainedToolEvidence(plan), token);
            RequireToolSuccess(plan);
            var content = plan.GetProperty("structuredContent").Clone();
            Assert.IsTrue(content.GetProperty("canPrepare").GetBoolean(), plan.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, content.GetProperty("errorCode").ValueKind, plan.GetRawText());
            return content;
        }

        // Apply the record's plan through the public tool: KiCad changed and saved, the XML published, and the same apply
        // replayed exactly.
        async Task<JsonElement> Apply(DesignRecoveryStore on, string name)
        {
            var args = new { instanceId, recoveryPath = on.StatePath, designPath = path, expectedRevisionToken = on.Read()!.RevisionToken,
                operationId = Guid.NewGuid().ToString("D") };
            var applied = await host.Tool("kicad_design_sync_apply", args);
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), RetainedToolEvidence(applied), token);
            if (applied.TryGetProperty("isError", out var failed) && failed.GetBoolean())
                await File.WriteAllTextAsync(Evidence(name + "-actual.xml"), SchematicDataXml.Write((await Capture()).Electrical.Hierarchy.Data), token);
            RequireToolSuccess(applied);
            var result = applied.GetProperty("structuredContent").Clone();
            Assert.IsTrue(result.GetProperty("nativeMutationCommitted").GetBoolean(), applied.GetRawText());
            Assert.IsTrue(result.GetProperty("nativeFilesSaved").GetBoolean(), applied.GetRawText());
            Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), applied.GetRawText());
            var replay = await host.Tool("kicad_design_sync_apply", args);
            RequireToolSuccess(replay);
            Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(), "The same apply replays exactly.");
            Assert.IsFalse(on.Read()!.State.HasPendingWork);
            return result;
        }

        // Apply an earlier record's plan through the public tool: KiCad already shows the design, so nothing is sent, saved or
        // written, and the apply succeeds (before, it failed: the coverage-list difference was taken for a settings change).
        async Task<JsonElement> ApplyUnchanged(DesignRecoveryStore on, string name, string designPath)
        {
            var before = await Capture();
            byte[] file = await File.ReadAllBytesAsync(designPath, token);
            var applied = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = on.StatePath, designPath,
                expectedRevisionToken = on.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), RetainedToolEvidence(applied), token);
            RequireToolSuccess(applied);
            var result = applied.GetProperty("structuredContent").Clone();
            Assert.IsFalse(result.GetProperty("nativeMutationCommitted").GetBoolean(), applied.GetRawText());
            Assert.IsFalse(result.GetProperty("nativeFilesSaved").GetBoolean(), applied.GetRawText());
            Assert.IsFalse(on.Read()!.State.HasPendingWork);
            Assert.AreEqual(before, await Capture(), name + ": KiCad is unchanged.");
            CollectionAssert.AreEqual(file, await File.ReadAllBytesAsync(designPath, token), name + ": the design itself did not change, so nothing is written.");
            return result;
        }

        // Records the same design would have had if preview 23 had saved them: the snapshots list library_cache as that build
        // did, third of four, and nothing else differs. One keeps electrical checkpoints (as the PSU/CPU flows write them), one
        // predates them (version 1, as records saved before electrical checkpoints are). Each is a separate record with its own
        // copy of the design file preview 23 published, for the same instance and KiCad; the journey's own record and design
        // file are untouched.
        async Task<object> EarlierPreviewRecords(StoredDesignRecovery record, byte[] currentXml, CheckedSchematicState shown)
        {
            static SchematicHierarchyData AsPreview23(SchematicHierarchyData data)
            {
                var result = data.Clone();
                foreach (var screen in result.Instances)
                {
                    Assert.AreEqual(3, screen.Metadata.UnrepresentedState.Count);
                    screen.Metadata.UnrepresentedState.Insert(2, "library_cache");
                }
                return result;
            }
            var state = record.State;
            var oldBaseline = state.Baseline with { Schematic = AsPreview23(state.Baseline.Schematic) };
            var oldObserved = AsPreview23(state.Observed);
            byte[] oldXml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(oldBaseline, state.KnowledgeLibraries));
            Assert.IsFalse(oldXml.AsSpan().SequenceEqual(currentXml), "Preview 23's XML lists library_cache.");
            var baselineElectrical = state.BaselineElectrical!.Clone(); baselineElectrical.Hierarchy.Data = oldBaseline.Schematic.Clone();
            var observedElectrical = state.ObservedElectrical!.Clone(); observedElectrical.Hierarchy.Data = oldObserved.Clone();
            string text = Encoding.UTF8.GetString(currentXml);
            var results = new List<object>();

            // (a) A record with electrical checkpoints: it reattaches and plans normally, with nothing to send to KiCad.
            var checkpointed = new DesignRecoveryStore(Path.Combine(earlierRecords, "preview23-recovery.json"));
            string checkpointedXml = Path.Combine(earlierRecords, "preview23-design.xml");
            await File.WriteAllBytesAsync(checkpointedXml, oldXml, token);
            checkpointed.Save(new(Guid.NewGuid(), state.InstanceId, state.NativeRevision, state.TrackingComplete, oldBaseline, oldXml, oldObserved,
                state.KnowledgeLibraries, BaselineElectrical: baselineElectrical, ObservedElectrical: observedElectrical), null);
            Assert.AreEqual(3, RecordVersion(checkpointed), "A record with electrical checkpoints.");
            var reattached = await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = checkpointed.StatePath,
                expectedRevisionToken = checkpointed.Read()!.RevisionToken, expectedDocumentEpoch = shown.State.Revision.Epoch });
            await File.WriteAllTextAsync(Evidence("preview23-reattach.json"), reattached.GetRawText(), token);
            RequireToolSuccess(reattached);
            Assert.IsTrue(reattached.GetProperty("structuredContent").GetProperty("snapshotChanged").GetBoolean(), "Only the coverage list changed.");
            var plan = await Plan(checkpointed, "preview23-plan");
            Assert.AreEqual(0, plan.GetProperty("nativeOperationsJson").GetArrayLength(), "Nothing to send to KiCad.");
            Assert.IsFalse(plan.GetProperty("nativeRebuildRequired").GetBoolean());
            Assert.IsFalse(plan.GetProperty("connectionRealizationRequired").GetBoolean());
            Assert.AreEqual(text, plan.GetProperty("candidateDesignXml").GetString(), "The planned design is the design as this build writes it.");
            Assert.AreEqual(shown, await Capture(), "Planning must not change KiCad.");
            await ApplyUnchanged(checkpointed, "preview23", checkpointedXml);
            var again = await Plan(checkpointed, "preview23-second-plan");
            Assert.AreEqual(0, again.GetProperty("nativeOperationsJson").GetArrayLength(), "Still nothing to send.");
            results.Add(new { record = "electrical checkpoints (version 3)", reattached = true, planned = true, applied = true, nativeOperations = 0 });

            // (b) A record saved before electrical checkpoints: planning names the one action that fixes it, which succeeds.
            var legacy = new DesignRecoveryStore(Path.Combine(earlierRecords, "preview23-v1-recovery.json"));
            string legacyXml = Path.Combine(earlierRecords, "preview23-v1-design.xml");
            await File.WriteAllBytesAsync(legacyXml, oldXml, token);
            legacy.Save(new(Guid.NewGuid(), state.InstanceId, state.NativeRevision, state.TrackingComplete, oldBaseline, oldXml, oldObserved,
                state.KnowledgeLibraries), null);
            Assert.AreEqual(1, RecordVersion(legacy), "A record without electrical checkpoints.");
            RequireToolSuccess(await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = legacy.StatePath,
                expectedRevisionToken = legacy.Read()!.RevisionToken, expectedDocumentEpoch = shown.State.Revision.Epoch }));
            var missing = await host.Tool("kicad_design_sync_plan", Recovery(legacy));
            await File.WriteAllTextAsync(Evidence("preview23-v1-plan.json"), RetainedToolEvidence(missing), token);
            Assert.AreEqual("missing_electrical_baseline", Error(missing), missing.GetRawText());
            string instruction = missing.GetProperty("structuredContent").GetProperty("errorMessage").GetString()!;
            StringAssert.Contains(instruction, "kicad_design_electrical_baseline_initialize", "The refusal names the one action that fixes it.");
            var initialized = await host.Tool("kicad_design_electrical_baseline_initialize", Recovery(legacy));
            await File.WriteAllTextAsync(Evidence("preview23-v1-initialize.json"), initialized.GetRawText(), token);
            RequireToolSuccess(initialized); // Before, electrical_baseline_mismatch: the coverage list was taken for a native edit.
            var legacyPlan = await Plan(legacy, "preview23-v1-initialized-plan");
            Assert.AreEqual(0, legacyPlan.GetProperty("nativeOperationsJson").GetArrayLength(), "Nothing to send to KiCad.");
            Assert.AreEqual(text, legacyPlan.GetProperty("candidateDesignXml").GetString(), "The planned design is the design as this build writes it.");
            await ApplyUnchanged(legacy, "preview23-v1", legacyXml);
            results.Add(new { record = "no electrical checkpoints (version 1)", refusal = "missing_electrical_baseline", instruction,
                initialized = true, planned = true, applied = true, nativeOperations = 0 });
            CollectionAssert.AreEqual(currentXml, await File.ReadAllBytesAsync(path, token), "The journey's own design file is untouched.");
            return results;
        }

        static int RecordVersion(DesignRecoveryStore on) => JsonNode.Parse(File.ReadAllText(on.StatePath))!["Version"]!.GetValue<int>();

        // Apply the rebuild through the public tool and require it to be refused before anything reaches KiCad: the code and
        // reason, KiCad, the project file (whatever it holds now), the XML and the recovery record all unchanged, and no
        // schematic file written.
        async Task<(string? Code, string? Message)> RefusedRebuild(string name, byte[] projectBytes, CheckedSchematicState shown)
        {
            var before = store.Read()!;
            var applied = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath = path,
                expectedRevisionToken = before.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), applied.GetRawText(), token);
            string? code = Error(applied);
            Assert.IsNotNull(code, applied.GetRawText());
            string? message = applied.GetProperty("structuredContent").GetProperty("errorMessage").GetString();
            Assert.AreEqual(shown, await Capture(), name + ": the refused rebuild changed nothing in KiCad.");
            CollectionAssert.AreEqual(projectBytes, await File.ReadAllBytesAsync(projectFile, token), name + ": the project file is kept as it is.");
            CollectionAssert.AreEqual(before.State.DesiredFileBytes, await File.ReadAllBytesAsync(path, token), name + ": the XML is untouched.");
            Assert.IsFalse(schematicFiles.Any(File.Exists), name + ": no schematic file was written.");
            Assert.AreEqual(before.RevisionToken, store.Read()!.RevisionToken, name + ": the recovery record is unchanged.");
            return (code, message);
        }

        // Replace the project's text variables in KiCad through the public checked-batch tool, at exactly this state.
        async Task<CheckedSchematicState> EditTextVariables(CheckedSchematicState at, Dictionary<string, string> textVariables, string description)
        {
            var variables = new SchematicTextVariableState(); variables.Variables.Add(textVariables);
            var request = new CheckedSchematicBatch { ExpectedState = at.State.Clone(), Batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(), DocumentEpoch = at.State.Revision.Epoch, ExpectedRevision = at.State.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), Description = description
            } };
            request.Batch.Operations.Add(new SchematicItemOperation { TargetDocument = document.Clone(), ReplaceTextVariables = variables });
            var edited = await host.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson = SchematicJson.Formatter.Format(request) });
            await File.WriteAllTextAsync(Evidence("text-variables-" + at.State.Revision.Sequence + ".json"), edited.GetRawText(), token);
            RequireToolSuccess(edited);
            return await Capture();
        }

        // KiCad creates the project's root schematic, which is missing, through the public tool: empty, never loaded or saved,
        // with the root identity the project declares.
        async Task<CheckedSchematicState> CreateRoot()
        {
            var createdRoot = await host.Tool("kicad_schematic_create", new { instanceId, path = schematicFiles[0] });
            RequireToolSuccess(createdRoot);
            var reopened = SchematicJson.Parser.Parse<DocumentSpecifier>(createdRoot.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            Assert.AreEqual(document, reopened, "KiCad creates the project's root with the root identity the project declares.");
            var state = await Until("the new empty root", s => s.Electrical.Hierarchy.Data.Instances.Count == 1);
            var root = state.Electrical.Hierarchy.Data.Instances.Single();
            Assert.IsEmpty(root.Items);
            Assert.AreEqual(0u, root.Metadata.LoadedNativeFormatVersion, "The new root was never loaded from a file.");
            return state;
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

    // Place and size a sheet symbol, with its name above and its file name below it as KiCad lays them out.
    private static void ArrangeSheet(SchematicScreenData screen, Guid sheetId, long x, long y, long height)
    {
        int index = screen.Items.ToList().FindIndex(i => i.Is(SheetSymbol.Descriptor) && i.Unpack<SheetSymbol>().Id.Value == sheetId.ToString("D"));
        Assert.IsGreaterThanOrEqualTo(0, index, "The sheet symbol " + sheetId + " is on its parent sheet.");
        var sheet = screen.Items[index].Unpack<SheetSymbol>();
        sheet.Position = new Vector2 { XNm = x, YNm = y };
        sheet.Size = new Vector2 { XNm = 38_100_000, YNm = height };
        sheet.NameField.Text.Position = new Vector2 { XNm = x, YNm = y };
        sheet.FilenameField.Text.Position = new Vector2 { XNm = x, YNm = y + height };
        screen.Items[index] = Any.Pack(sheet);
    }

    // How many objects of each drawn kind KiCad shows across the hierarchy: labels, wires and sheet pins.
    private static Dictionary<string, int> DrawnObjects(SchematicHierarchyData data)
    {
        var items = data.Instances.SelectMany(s => s.Items).ToArray();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["LocalLabel"] = items.Count(i => i.Is(LocalLabel.Descriptor)),
            ["HierarchicalLabel"] = items.Count(i => i.Is(HierarchicalLabel.Descriptor)),
            ["SchematicLine"] = items.Count(i => i.Is(SchematicLine.Descriptor)),
            ["SheetPin"] = items.Where(i => i.Is(SheetSymbol.Descriptor)).Sum(i => i.Unpack<SheetSymbol>().Pins.Count)
        };
    }

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
