using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using DocumentRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9).
    // Lane 2A replaces this body when it delivers the journey.
    private static Task VerifyPsuCpuConnectedRealization(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
        => throw new AssertInconclusiveException("Phase 2 lane 2A has not delivered this journey");

    // Creation-only PSU/CPU journey on the S1 seed (ledger p95e0c19e6143deb6): the frozen fixture's
    // Components stage written as XML, laid out, prepared and applied through the public MCP tools over
    // STDIO. The live editor must then hold every component with its exact pins on its sheet, processor
    // U5 as one component and one declared definition whose units 1-3 sit on CPU and unit 4 on CPU_POWER,
    // and every pin unconnected except the regulator U2's pins 1 and 4, which its symbol draws at one point
    // and KiCad therefore joins (psu-cpu-fixture-and-ownership.md §1.4.2, §1.6.3 and erratum 2026-09-24).
    // XML that puts those two pins on different nets is refused while planning, before and after creation.
    // One native undo/redo and one save/reload follow, and the recovery record reattaches to the reloaded
    // editor unchanged.
    private static async Task VerifyPsuCpuComponentCreation(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        const PsuCpuStage Stage = PsuCpuStage.Components;
        var expected = PsuCpuFixture.ExpectedNative(Stage);
        var document = context.Root;
        var seedBaseline = context.Baseline ?? throw new AssertFailedException("The S1 seed must provide a native baseline.");
        string path = context.DesignPath;
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-psu-cpu-" + name);
        var store = new DesignRecoveryStore(Evidence("recovery.json"));
        // Workaround for a tracked defect, not a claim about users' projects. The harness writes the S1 sheet files directly,
        // so KiCad has never saved this project: its project file has no sheet list and none of KiCad's schematic settings. The
        // apply's own save then writes both into the project file, the saved state no longer matches the state recorded before
        // saving, and publication refuses the apply after KiCad has already saved (native_save_not_confirmed; runs
        // t20260924T063222Z-849a5a and t20260924T064900Z-59a396, recorded in the nativeSave observation of apply-connectivity.json).
        // Any project whose project file KiCad has not yet written in its own form would hit the same refusal. That defect belongs
        // to the synchronization and seed owners (lanes 2C/2D and the parent) and is reported for a ledger outcome; saving once
        // here keeps it out of this creation journey.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        Assert.IsFalse((await Capture()).State.NativeContentDirty, "The S1 seed is saved by KiCad before XML creation.");
        var saved = await PsuCpuFixture.InitializeRecoveryAsync(client, context, store.StatePath, token);
        var desired = PsuCpuFixture.Desired(context, Stage);
        var circuit = desired.Engineering.Circuit;
        Assert.HasCount(8, circuit.Components); Assert.HasCount(11, circuit.Symbols); Assert.IsEmpty(circuit.Nets);
        Assert.IsTrue(circuit.Symbols.All(s => s.Placement is null), "The frozen fixture occurrences are coordinate-free.");
        Assert.AreEqual(222, circuit.Parts.Sum(p => p.Pins.Count));
        byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
        await File.WriteAllBytesAsync(path, bytes, token);
        saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
        var seeded = await Capture();
        Assert.IsTrue(expected.Symbols.All(s => circuit.Symbols.Any(o => o.Id == s.Occurrence)));

        // Explicit fixture policy (§1.6.3 presentation): a 10 mm page inset and the bottom 50 mm kept
        // for the title block, for every sheet that receives a symbol. The root sheet receives none.
        string PathKey(IEnumerable<Guid> ids) => string.Join('/', ids.Select(id => id.ToString("D")));
        var sheetPaths = expected.Sheets.ToDictionary(s => s.Key,
            s => PathKey(seedBaseline.SheetBindings.Single(b => b.SheetInstanceId == s.ModelSheetInstance).NativePath));
        var targetSheets = expected.Sheets.Where(s => expected.Symbols.Any(x => x.Sheet == s.Key)).ToArray();
        CollectionAssert.AreEquivalent(new[] { "PSU", "CPU", "CPU_POWER" }, targetSheets.Select(s => s.Key).ToArray());
        var regions = new List<SchematicLayoutRegion>();
        var usable = new Dictionary<string, PresentationBounds>(StringComparer.Ordinal);
        foreach (var sheet in targetSheets)
        {
            var screen = seedBaseline.Schematic.Instances.Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == sheetPaths[sheet.Key]);
            var page = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = screen.Metadata.Document.Clone(), ExpectedRevision = seeded.State.Revision.Clone() }, token);
            Assert.AreEqual(sheet.NativeScreen.ToString("D"), page.ScreenId.Value, sheet.Key);
            long inset = expected.Presentation.PageInsetMm * 1_000_000L, reserve = expected.Presentation.ReservedBottomMm * 1_000_000L;
            var bounds = new PresentationBounds(page.PageBounds.Position.XNm + inset, page.PageBounds.Position.YNm + inset,
                page.PageBounds.Position.XNm + page.PageBounds.Size.XNm - inset, page.PageBounds.Position.YNm + page.PageBounds.Size.YNm - reserve);
            regions.Add(new(sheet.NativeScreen, bounds, []));
            usable.Add(sheet.Key, bounds);
        }

        Guid operation = Guid.NewGuid();
        string? coordinateFreeCode;
        int publicPlanCreates;
        JsonElement applied;
        CheckedSchematicState created;
        SchematicDesign synchronized;
        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(), Evidence("host"), Evidence("host.log"), token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        object Recovery(string revision) => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = revision };

        // Preparing the coordinate-free XML refuses creation until layout places every unit.
        var unplaced = await host.Tool("kicad_design_sync_plan", Recovery(saved.RevisionToken));
        await File.WriteAllTextAsync(Evidence("coordinate-free-plan.json"), RetainedToolEvidence(unplaced), token);
        Assert.IsTrue(unplaced.GetProperty("isError").GetBoolean(), unplaced.GetRawText());
        coordinateFreeCode = unplaced.GetProperty("structuredContent").GetProperty("errorCode").GetString();
        Assert.AreEqual("created_symbol_placement_required", coordinateFreeCode, unplaced.GetRawText());

        var layout = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = saved.RevisionToken, gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L, regions,
            userInstructions = "Place each fixture component on its own sheet and keep the processor's power unit on CPU_POWER." });
        await File.WriteAllTextAsync(Evidence("initial-layout.json"), RetainedToolEvidence(layout), token);
        RequireToolSuccess(layout);
        var proposal = layout.GetProperty("structuredContent");
        Assert.IsTrue(proposal.GetProperty("canPropose").GetBoolean(), proposal.GetRawText());
        CollectionAssert.AreEquivalent(circuit.Symbols.Select(s => s.Id).ToArray(), proposal.GetProperty("refinement").GetProperty("AffectedSymbols")
            .EnumerateArray().Select(s => Guid.Parse(s.GetString()!)).ToArray());
        Assert.AreEqual(seeded, await Capture(), "Layout preparation must not change the native document.");
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
        string proposedXml = proposal.GetProperty("desiredXml").GetString()!;
        var placed = SchematicDesignXml.Read(proposedXml, []);
        Assert.AreEqual(CircuitXml.Write(circuit), CircuitXml.Write(placed.Engineering.Circuit.WithoutPlacement()),
            "Layout only adds coordinates; every unit stays on the sheet its occurrence names.");
        Assert.IsTrue(placed.Engineering.Circuit.Symbols.All(s => s.Placement is not null));
        bytes = Encoding.UTF8.GetBytes(proposedXml);
        await File.WriteAllBytesAsync(path, bytes, token);
        saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
        var connectionIntent = await RequirePsuCpuConnectionIntent(placed);

        // The public preview is exactly the planner's creation candidate: eleven new native symbols.
        var planner = SchematicSynchronizationPlanner.Plan(saved.State, token);
        Assert.IsTrue(planner.CanPrepare, planner.ErrorCode + ": " + planner.ErrorMessage);
        var prepared = await host.Tool("kicad_design_sync_plan", Recovery(saved.RevisionToken));
        await File.WriteAllTextAsync(Evidence("plan.json"), RetainedToolEvidence(prepared), token);
        RequireToolSuccess(prepared);
        var preview = prepared.GetProperty("structuredContent");
        Assert.AreEqual(planner.CandidateXml, preview.GetProperty("candidateDesignXml").GetString());
        CollectionAssert.AreEqual(planner.NativeOperations.Select(o => SchematicJson.Formatter.Format(o)).ToArray(),
            preview.GetProperty("nativeOperationsJson").EnumerateArray().Select(o => o.GetString()).ToArray());
        publicPlanCreates = planner.NativeOperations.Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true);
        Assert.AreEqual(expected.Symbols.Count, publicPlanCreates);
        Assert.AreEqual(seeded, await Capture(), "Planning must not change the native document.");

        var args = new { instanceId, recoveryPath = store.StatePath, designPath = path, expectedRevisionToken = saved.RevisionToken,
            operationId = operation.ToString("D") };
        string projectFile = Path.Combine(context.ProjectDirectory, "fixture.kicad_pro");
        byte[] projectBeforeApply = await File.ReadAllBytesAsync(projectFile, token);
        applied = await host.Tool("kicad_design_sync_apply", args);
        await File.WriteAllTextAsync(Evidence("apply.json"), applied.GetRawText(), token);
        if (applied.TryGetProperty("isError", out var failed) && failed.GetBoolean())
            await ObserveRefusedApply();
        RequireToolSuccess(applied);
        var result = applied.GetProperty("structuredContent");
        Assert.IsTrue(result.GetProperty("nativeMutationCommitted").GetBoolean(), result.GetRawText());
        Assert.IsTrue(result.GetProperty("nativeFilesSaved").GetBoolean(), result.GetRawText());
        Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), result.GetRawText());
        var replay = await host.Tool("kicad_design_sync_apply", args); RequireToolSuccess(replay);
        Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());

        synchronized = store.Read()!.State.Baseline;
        // The file holds exactly the previewed candidate. The recovery baseline is that same design with KiCad's own item
        // enumeration kept, because the executor never rewrites XML for enumeration alone.
        string publishedXml = await File.ReadAllTextAsync(path, token);
        Assert.AreEqual(planner.CandidateXml, publishedXml, "The published XML is the previewed candidate.");
        var publishedDesign = SchematicDesignXml.Read(publishedXml, []);
        Assert.AreEqual(publishedXml, SchematicDesignXml.Write(synchronized with { Schematic = publishedDesign.Schematic }, []),
            "The synchronized design is the published one apart from native item order.");
        Assert.IsTrue(Same(publishedDesign.Schematic, synchronized.Schematic), "The synchronized native objects are the published ones.");
        created = await Capture();
        Assert.IsFalse(created.State.NativeContentDirty, "Apply saves the created sheets.");
        var afterApply = RequireCreated(created, "after apply");
        await CheckPresentation(created);
        var stackedAfterApply = await RequireStackedPinsOnCreatedSymbols();

        // One native undo removes every created symbol from all three sheets; redo restores them exactly.
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        var undone = await Until("native undo", s => Same(WithoutLibraryCache(s.Electrical.Hierarchy.Data), WithoutLibraryCache(seedBaseline.Schematic)));
        Assert.IsFalse(undone.Electrical.Hierarchy.Data.Instances.Any(s => s.Items.Any(i => i.Is(SchematicSymbolInstance.Descriptor))),
            "Undo must remove every created unit, including U5 unit 4 on CPU_POWER.");
        // Unused library cache entries carry no drawn object; whether undo also drops them is recorded.
        bool undoRestoredLibraryCache = Same(undone.Electrical.Hierarchy.Data, seedBaseline.Schematic);
        await FocusedSchematicShortcut(client, document, processId, display, "y", token);
        var redone = await Until("native redo", s => Same(s.Electrical.Hierarchy.Data, synchronized.Schematic));
        RequireCreated(redone, "after redo");

        // Save the redone editor, reload the saved sheets from disk and require the same schematic.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        Assert.IsFalse((await Capture()).State.NativeContentDirty);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        var reloaded = await Capture();
        var loadedExpected = synchronized.Schematic.Clone();
        foreach (var screen in loadedExpected.Instances)
        {
            // Loading the newly written files changes source-format provenance, not any persisted object.
            var actual = reloaded.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document));
            Assert.AreEqual(screen.Metadata.WriterNativeFormatVersion, actual.Metadata.LoadedNativeFormatVersion);
            screen.Metadata.LoadedNativeFormatVersion = actual.Metadata.LoadedNativeFormatVersion;
        }
        if (!Same(reloaded.Electrical.Hierarchy.Data, loadedExpected))
        {
            await File.WriteAllTextAsync(Evidence("reloaded.xml"), SchematicDataXml.Write(reloaded.Electrical.Hierarchy.Data), token);
            Assert.Fail("The reloaded sheets must equal the created schematic.");
        }
        Assert.IsFalse(reloaded.State.NativeContentDirty);
        Assert.AreNotEqual(created.State.Revision.Epoch, reloaded.State.Revision.Epoch, "Revert reloads a new native document.");
        var afterReload = RequireCreated(reloaded, "after reload");

        // The recovery record adopts the reloaded editor unchanged, and the settled plan sends nothing to the editor.
        var beforeReattach = store.Read()!;
        var reattach = await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = beforeReattach.RevisionToken, expectedDocumentEpoch = reloaded.State.Revision.Epoch });
        RequireToolSuccess(reattach);
        Assert.AreEqual(SchematicDesignXml.Write(beforeReattach.State.Baseline, []), SchematicDesignXml.Write(store.Read()!.State.Baseline, []));
        var settled = await host.Tool("kicad_design_sync_plan", Recovery(store.Read()!.RevisionToken));
        await File.WriteAllTextAsync(Evidence("reloaded-plan.json"), RetainedToolEvidence(settled), token);
        RequireToolSuccess(settled);
        Assert.AreEqual(0, settled.GetProperty("structuredContent").GetProperty("nativeOperationsJson").GetArrayLength(), settled.GetRawText());
        Assert.AreEqual(reloaded, await Capture());
        // What the settled preview would publish, asserted exactly. Two differences from the published XML are expected:
        // 1. Loaded-format provenance. The S1 seed files were written in an older format, KiCad saved them in its own, and the
        //    reload records that; by design such a reload is published to the XML, never sent to KiCad (the executor's
        //    Equivalent rule). Every screen's provenance must be exactly what the reloaded editor reports.
        // 2. A tracked lane 2C defect, not intended behaviour: the general reconciliation (SchematicNetReconciliation) still
        //    reads KiCad's join of U2's stacked pins as a native edit and adds a generated net holding exactly that pair, so an
        //    apply or automatic synchronization would add a net the user never wrote although KiCad shows exactly what the XML
        //    describes (decision kicad-stacked-pins-one-node-20260924; reported for a ledger outcome linked to
        //    p95e0c19e6143deb6). When lane 2C's fix lands, the added nets below must become empty.
        // Nothing else may differ: not the library cache order, not any object, field or net.
        byte[] published = await File.ReadAllBytesAsync(path, token);
        string publishedText = await File.ReadAllTextAsync(path, token);
        Assert.AreEqual(publishedXml, publishedText, "Save, reload and reattachment leave the published XML file untouched.");
        var settledContent = settled.GetProperty("structuredContent");
        Assert.IsTrue(settledContent.GetProperty("canPrepare").GetBoolean(), settled.GetRawText());
        Assert.AreEqual(0, settledContent.GetProperty("netChanges").GetArrayLength(), "No published net loses its identity.");
        Assert.AreEqual(0, settledContent.GetProperty("electricalConflicts").GetArrayLength(), "The settled plan has no electrical conflict.");
        string settledXml = settledContent.GetProperty("candidateDesignXml").GetString()
            ?? throw new AssertFailedException("The settled plan must preview its candidate XML.");
        var settledDesign = SchematicDesignXml.Read(settledXml, []);
        var publishedNets = publishedDesign.Engineering.Circuit.Nets.Select(n => n.Id).ToHashSet();
        var references = synchronized.Engineering.Circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
        var addedNets = settledDesign.Engineering.Circuit.Nets.Where(n => !publishedNets.Contains(n.Id)).ToArray();
        CollectionAssert.AreEquivalent(expected.JoinedPins.Select(g => string.Join(",", g.Select(p => p.Reference + "." + p.Number).Order(StringComparer.Ordinal))).ToArray(),
            addedNets.Select(n => string.Join(",", n.Pins.Select(p => references[p.ComponentId] + "." + p.Pin).Order(StringComparer.Ordinal))).ToArray(),
            "Tracked lane 2C defect: the settled plan adds exactly one generated net for each group of stacked pins KiCad joins. "
            + "Once lane 2C merges the comparison's stacked pins into the model partitions, no net may be added.");
        var reloadedProvenance = reloaded.Electrical.Hierarchy.Data.Instances.ToDictionary(s => s.Metadata.Document, s => s.Metadata.LoadedNativeFormatVersion);
        var provenanceUpdates = publishedDesign.Schematic.Instances.Count(s => s.Metadata.LoadedNativeFormatVersion != reloadedProvenance[s.Metadata.Document]);
        var expectedSettled = publishedDesign with { Schematic = publishedDesign.Schematic.Clone() };
        foreach (var screen in expectedSettled.Schematic.Instances)
            screen.Metadata.LoadedNativeFormatVersion = reloadedProvenance[screen.Metadata.Document];
        Assert.AreEqual(publishedText, SchematicDesignXml.Write(publishedDesign, []), "The published XML reads and writes back unchanged.");
        Assert.AreEqual(SchematicDesignXml.Write(expectedSettled, []), SchematicDesignXml.Write(settledDesign with { Engineering = settledDesign.Engineering with { Circuit =
                settledDesign.Engineering.Circuit with { Nets = [.. settledDesign.Engineering.Circuit.Nets.Where(n => publishedNets.Contains(n.Id))] } } }, []),
            "Apart from the reloaded files' provenance and the generated nets, the settled candidate is exactly the published XML.");
        var settledPlan = new
        {
            nativeOperations = 0, keepsPublishedXml = settledXml == publishedText, provenanceUpdates,
            addedNets = addedNets.Select(n => new { n.Name, pins = n.Pins.Select(p => references[p.ComponentId] + "." + p.Pin).ToArray() }).ToArray(),
            onlyOtherDifferences = "loaded-format provenance of the reloaded screens"
        };

        // The published XML is retained in the recovery record; the proof names it by length and SHA-256.
        await NativeKeyboard.CaptureAsync(display, Evidence("window.png"), token);
        await File.WriteAllTextAsync(Evidence("proof.json"), JsonSerializer.Serialize(new
        {
            instanceId, fixture = "psu-cpu", PsuCpuFixture.Version, stage = Stage.ToString(), seed = context.Seed.ToString(), operation,
            realStdioApply = true, coordinateFreePlanErrorCode = coordinateFreeCode, initialLayoutProposed = true,
            publicPlanMatchesPlanner = true, publicPlanSymbolCreates = publicPlanCreates, exactReplay = true,
            placements = afterApply.Placements, declaredPins = circuit.Parts.Sum(p => p.Pins.Count), modelNets = circuit.Nets.Count,
            observedAfterApply = afterApply.Connectivity, observedAfterReload = afterReload.Connectivity,
            publishedXml = new { length = published.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(published)) },
            processorUnit4Sheet = "CPU_POWER", processorOneComponentOneDefinition = true,
            stackedPinsJoinedByKiCad = expected.JoinedPins.Select(g => g.Select(p => p.Reference + "." + p.Number).ToArray()).ToArray(),
            stackedPinsOnCreatedSymbols = stackedAfterApply,
            nativeUndoRedoVerified = true, undoRestoredLibraryCache, saveReloadVerified = true, recoveryReattachedWithoutChanges = true,
            settledPlan, presentation = expected.Presentation, crossPlatformReady = false, connectionIntent
        }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

        // CN-1 connection intent for the fixture's Complete stage (cn1-wiring-intent.md §5), planned from this editor's
        // real S1 capture, the part symbols it captured and the laid-out placements, as an editor advertising
        // schematic.connection-realization.v1 would receive it. This editor does not advertise it and nothing can draw
        // the connections yet, so the plan is only inspected against expected-native.json: every net, crossing,
        // hierarchical label, sheet pin and native group. Nothing is saved, published or sent to the editor.
        async Task<object> RequirePsuCpuConnectionIntent(SchematicDesign layout)
        {
            var session = await client.HandshakeAsync(token);
            Assert.AreEqual(instanceId, session.InstanceId);
            Assert.IsFalse(session.Capabilities.Contains(SchematicConnectedAddition.NativeCapability),
                "This editor must not advertise connection realization before every native piece exists: " + string.Join(",", session.Capabilities));
            var advertised = session.Clone(); advertised.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
            var current = store.Read()!;
            var nativeBefore = await Capture();
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            var nets = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets;
            var connected = layout with { Engineering = layout.Engineering with { Circuit = layout.Engineering.Circuit with { Nets = nets } } };
            var revision = current.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(connected, [])) };
            var gated = SchematicSynchronizationPlanner.Plan(revision, session, token);
            Assert.IsNull(gated.Connections, "This editor's own handshake admits no wiring.");
            var plan = SchematicSynchronizationPlanner.Plan(revision, advertised, token);
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
            SchematicSynchronizationPlanTests.RequirePsuCpuIntent(intent, plan.Candidate!, created: true);
            // Must-catch on this editor's own captured LP3982 geometry: moving U2.4 from RAIL_B to LDO_FAULT splits the pins
            // its symbol draws at one point, which KiCad always joins, so creating and connecting it is refused while planning.
            var split = current.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(layout with { Engineering = layout.Engineering with
                { Circuit = layout.Engineering.Circuit with { Nets = SplitStackedPins(nets, layout.Engineering.Circuit) } } }, [])) };
            var refused = RequireStackedRefusal(SchematicSynchronizationPlanner.Plan(split, advertised, token), "create and connect");
            Assert.AreEqual(nativeBefore, await Capture(), "Planning connections must not change the native document.");
            Assert.AreEqual(current.RevisionToken, store.Read()!.RevisionToken, "Planning connections must not advance recovery.");
            CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Planning connections must not publish XML.");
            await File.WriteAllTextAsync(Evidence("connection-intent.json"), JsonSerializer.Serialize(new
                { gatedErrorCode = gated.ErrorCode, intent = SchematicConnectionIntentBuilder.Summary(intent), stackedSplit = refused }), token);
            return new { gatedErrorCode = gated.ErrorCode, nets = intent.Nets.Count, islands = intent.Screens.Sum(s => s.Islands.Count),
                ports = intent.Ports.Count, expectedGroups = intent.ExpectedGroups.Count, createdSymbols = intent.CreatedSymbolIds.Count,
                stackedSplit = refused };
        }

        // The same rules once KiCad shows the created components with U2's stacked pins joined: splitting them over two nets is
        // refused before anything is sent, while the frozen Complete nets (both pins on RAIL_B) still plan over KiCad's joined
        // pair, predicting it inside RAIL_B's native group. Nothing is saved, published or sent to the editor.
        async Task<object> RequireStackedPinsOnCreatedSymbols()
        {
            var session = await client.HandshakeAsync(token);
            var advertised = session.Clone(); advertised.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
            var current = store.Read()!;
            Assert.IsFalse(current.State.HasPendingWork);
            var nativeBefore = await Capture();
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets;
            var baseline = current.State.Baseline;
            DesignRecoveryState Revision(IReadOnlyList<CircuitNet> nets) => current.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
                baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with { Nets = nets } } }, [])) };
            var refused = RequireStackedRefusal(SchematicSynchronizationPlanner.Plan(Revision(SplitStackedPins(complete, baseline.Engineering.Circuit)), advertised, token),
                "connect only");
            var planned = SchematicSynchronizationPlanner.Plan(Revision(complete), advertised, token);
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(planned);
            SchematicSynchronizationPlanTests.RequirePsuCpuIntent(intent, planned.Candidate!, created: false);
            var u2 = baseline.Engineering.Circuit.Components.Single(c => c.Reference == "U2").Id;
            var stackedKeys = SchematicConnectionIntentBuilderTests.Keys(planned.Candidate!, (u2, "1"), (u2, "4"));
            Assert.IsTrue(intent.ExpectedGroups.Any(g => stackedKeys.All(k => g.Contains(k))), "RAIL_B's expected native group holds both stacked pins.");
            Assert.AreEqual(nativeBefore, await Capture(), "Planning connections must not change the native document.");
            Assert.AreEqual(current.RevisionToken, store.Read()!.RevisionToken, "Planning connections must not advance recovery.");
            CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Planning connections must not publish XML.");
            return new { stackedSplit = refused, completeNetsPlanned = intent.Nets.Count, expectedGroups = intent.ExpectedGroups.Count };
        }

        // U2.4 moved from RAIL_B into LDO_FAULT, away from the pin 1 its symbol stacks it on.
        static IReadOnlyList<CircuitNet> SplitStackedPins(IReadOnlyList<CircuitNet> nets, Circuit circuit)
        {
            var stacked = new PinEndpoint(circuit.Components.Single(c => c.Reference == "U2").Id, "4");
            Assert.IsTrue(nets.Single(n => n.Name == "RAIL_B").Pins.Contains(stacked));
            return [.. nets.Select(n => n.Name == "RAIL_B" ? n with { Pins = [.. n.Pins.Where(p => p != stacked)] }
                : n.Name == "LDO_FAULT" ? n with { Pins = [.. n.Pins, stacked] } : n)];
        }

        static object RequireStackedRefusal(SchematicSynchronizationPlan plan, string path)
        {
            Assert.AreEqual(SchematicConnectionErrors.StackedPinsOnDifferentNets, plan.ErrorCode, path + ": " + plan.ErrorMessage);
            StringAssert.Contains(plan.ErrorMessage!, "U2.1 in net 'RAIL_B' and U2.4 in net 'LDO_FAULT'", path);
            Assert.IsNull(plan.Candidate, path); Assert.IsNull(plan.CandidateXml, path); Assert.IsNull(plan.Connections, path);
            Assert.IsEmpty(plan.NativeOperations, path + ": nothing may reach the editor.");
            return new { path, errorCode = plan.ErrorCode, errorMessage = plan.ErrorMessage, nativeOperations = plan.NativeOperations.Count };
        }

        static SchematicHierarchyData WithoutLibraryCache(SchematicHierarchyData data)
        {
            var result = data.Clone();
            foreach (var screen in result.Instances) screen.CachedSymbols.Clear();
            return result;
        }

        bool Same(SchematicHierarchyData left, SchematicHierarchyData right) =>
            SchematicHierarchyDelta.Plan(left, right, token).Count == 0 && SchematicHierarchyDelta.Plan(right, left, token).Count == 0;

        // A native shortcut is processed asynchronously; read checked state until it takes effect.
        async Task<CheckedSchematicState> Until(string what, Func<CheckedSchematicState, bool> reached)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var state = await Capture();
                if (reached(state)) return state;
                try { await Task.Delay(250, wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    await File.WriteAllTextAsync(Evidence(what.Replace(' ', '-') + "-actual.xml"), SchematicDataXml.Write(state.Electrical.Hierarchy.Data), token);
                    throw new AssertFailedException(what + " did not reach the expected schematic within 20 s.");
                }
            }
        }

        // A refused apply is observed, never accepted: what KiCad shows, every pin group whose native connectivity
        // differs from the planned candidate (by reference and pin number), whether the XML file and the recovery
        // record stayed unpublished, whether KiCad holds exactly the planned symbols, and what the public recovery
        // tools then let a user do. These are recorded as observations, not asserted, so a half-applied state is
        // never locked in as expected behaviour; the journey still fails on the refused apply.
        async Task ObserveRefusedApply()
        {
            var actual = await Capture();
            await File.WriteAllTextAsync(Evidence("apply-actual.xml"), SchematicDataXml.Write(actual.Electrical.Hierarchy.Data), token);
            var candidate = planner.Candidate!;
            var references = candidate.Engineering.Circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
            var observed = SchematicElectricalComparison.Compare(candidate, actual.Electrical, [], token);
            var planned = NativeSymbolKeys(candidate.Schematic);
            var drawn = NativeSymbolKeys(actual.Electrical.Hierarchy.Data);
            bool bindingsResolve;
            try { bindingsResolve = SchematicModelProjection.NativeSymbols(candidate, actual.Electrical.Hierarchy.Data).Count == candidate.SymbolBindings.Count; }
            catch (KeyNotFoundException) { bindingsResolve = false; }
            string before = SchematicDesignXml.Write(saved.State.Baseline, []);
            await File.WriteAllTextAsync(Evidence("apply-connectivity.json"), JsonSerializer.Serialize(new
            {
                observed.PinBindingsComplete, observed.ConnectivityEquivalent,
                issues = observed.Issues.Select(i => new { i.Code, i.NativePath, i.NativeId, i.ComponentId }),
                differences = observed.Differences.Select(d => new { d.Kind, d.ModelNetIds,
                    pins = d.Pins.Select(p => references.GetValueOrDefault(p.ComponentId, p.ComponentId.ToString("D")) + "." + p.Pin) }),
                multiPinNativeNets = (observed.PinPartitions ?? []).Where(p => p.Pins.Count > 1).Select(p => new { p.NativeName,
                    pins = p.Pins.Select(x => references.GetValueOrDefault(x.ComponentId, x.ComponentId.ToString("D")) + "." + x.Pin) }),
                refusedApply = new
                {
                    errorCode = ErrorCode(applied), xmlUnchanged = await XmlUnchanged(), recovery = RecoveryObservation(before),
                    nativeDirty = actual.State.NativeContentDirty, nativeSymbols = drawn.Count, plannedSymbols = planned.Count,
                    nativeHoldsExactlyPlannedSymbols = drawn.SetEquals(planned), plannedBindingsResolveOnPlannedSheets = bindingsResolve,
                    nativeObjectsMatchCandidate = Same(actual.Electrical.Hierarchy.Data, candidate.Schematic),
                    nativeSave = await NativeSaveObservation(actual.State)
                }
            }), token);

            // What a user can do next through the public tools: inspect the record, resume the same operation,
            // refresh the record from KiCad, then discard the unsaved editor change by reloading the saved sheets
            // and ask the record to adopt that reload and plan again.
            var attempts = new List<object>
            {
                await Attempt("kicad_design_recovery_plan", new { instanceId, recoveryPath = store.StatePath }),
                await Attempt("kicad_design_sync_apply", args),
                await Attempt("kicad_design_recovery_refresh", new { instanceId, recoveryPath = store.StatePath,
                    expectedRevisionToken = store.Read()!.RevisionToken })
            };
            object reload;
            try
            {
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
                var reverted = await Capture();
                reload = new { reloadedSavedSheets = true, nativeSymbols = NativeSymbolKeys(reverted.Electrical.Hierarchy.Data).Count,
                    equalsSeed = Same(reverted.Electrical.Hierarchy.Data, seedBaseline.Schematic), nativeDirty = reverted.State.NativeContentDirty };
                attempts.Add(await Attempt("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
                    expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = reverted.State.Revision.Epoch }));
                attempts.Add(await Attempt("kicad_design_sync_plan", Recovery(store.Read()!.RevisionToken)));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                reload = new { reloadedSavedSheets = false, exception = error.GetType().Name, error.Message };
            }
            var last = store.Read()!;
            await File.WriteAllTextAsync(Evidence("apply-recovery.json"), JsonSerializer.Serialize(new
            {
                attempts, reload, finalRecovery = RecoveryObservation(before), finalXmlUnchanged = await XmlUnchanged(),
                userCanLeaveHalfAppliedState = !last.State.HasPendingWork
            }), token);

            async Task<object> Attempt(string tool, object arguments)
            {
                try
                {
                    var outcome = await host.Tool(tool, arguments);
                    var native = await Capture();
                    return new
                    {
                        tool, isError = outcome.TryGetProperty("isError", out var error) && error.GetBoolean(), errorCode = ErrorCode(outcome),
                        result = outcome.TryGetProperty("structuredContent", out var content) ? RetainedJson(content) : null,
                        message = outcome.TryGetProperty("structuredContent", out _) ? null : Text(outcome) is { } text && text.Length > 2000 ? text[..2000] : Text(outcome),
                        recovery = RecoveryObservation(before), xmlUnchanged = await XmlUnchanged(),
                        nativeSymbols = NativeSymbolKeys(native.Electrical.Hierarchy.Data).Count, nativeDirty = native.State.NativeContentDirty
                    };
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return new { tool, exception = error.GetType().Name, error.Message };
                }
            }
            async Task<bool> XmlUnchanged() => (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(bytes);
            // The save confirmation compares the recorded pre-save state with KiCad's state after saving: the same state
            // digest, clean content, project settings included and every native file covered. Which of these differs, and
            // whether saving rewrote the project file, is recorded with both project files.
            async Task<object?> NativeSaveObservation(DocumentLifecycleState after)
            {
                var expectedSave = store.Read()!.State.PendingNativeSave?.ExpectedState;
                byte[] projectAfter = await File.ReadAllBytesAsync(projectFile, token);
                await File.WriteAllBytesAsync(Evidence("project-before-apply.kicad_pro"), projectBeforeApply, token);
                await File.WriteAllBytesAsync(Evidence("project-after-apply.kicad_pro"), projectAfter, token);
                return expectedSave is null ? null : new
                {
                    sameStateSha256 = after.StateSha256 == expectedSave.StateSha256, sameNativeIdentity = after.NativeIdentity == expectedSave.NativeIdentity,
                    sameRevisionEpoch = after.Revision?.Epoch == expectedSave.Revision?.Epoch, sequenceBefore = expectedSave.Revision?.Sequence,
                    sequenceAfter = after.Revision?.Sequence, after.NativeContentDirty, after.ProjectSettingsIncluded,
                    fileCoverage = CheckedSchematicContract.FileCoverage(after), projectFileRewritten = !projectAfter.AsSpan().SequenceEqual(projectBeforeApply),
                    files = after.FileBaselines.Select(f => new { f.Path, status = f.Status.ToString(), f.BaselineKnown, f.CurrentKnown,
                        f.BaselineExists, f.CurrentExists, sameSha256 = f.BaselineSha256 == f.CurrentSha256, f.BaselineBytes, f.CurrentBytes }).ToArray(),
                    nativeFiles = after.NativeFiles.ToArray()
                };
            }
            object RecoveryObservation(string baselineBefore)
            {
                var state = store.Read()!.State;
                return new
                {
                    pendingWork = state.HasPendingWork, pendingMutation = state.PendingMutation is not null,
                    pendingNativeSave = state.PendingNativeSave is not null, pendingPublication = state.PendingPublication?.Phase.ToString(),
                    lastSynchronization = state.LastSynchronization?.OperationId,
                    baselineAdvanced = SchematicDesignXml.Write(state.Baseline, []) != baselineBefore
                };
            }
            // A refusal carries its code in the structured result or, for tools without one, in its JSON text.
            static string? ErrorCode(JsonElement outcome)
            {
                if (outcome.TryGetProperty("structuredContent", out var content) && content.ValueKind == JsonValueKind.Object
                    && content.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                    return code.GetString();
                try
                {
                    using var text = JsonDocument.Parse(Text(outcome) ?? "");
                    if (text.RootElement.ValueKind != JsonValueKind.Object) return null;
                    foreach (var name in new[] { "errorCode", "code" })
                        if (text.RootElement.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.String) return inner.GetString();
                    return null;
                }
                catch (JsonException) { return null; }
            }
            static string? Text(JsonElement outcome) => outcome.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array
                ? string.Concat(blocks.EnumerateArray().Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString())) : null;
        }

        static HashSet<string> NativeSymbolKeys(SchematicHierarchyData data) => data.Instances.SelectMany(screen => screen.Items
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value)) + "#" + i.Unpack<SchematicSymbolInstance>().Id.Value))
            .ToHashSet(StringComparer.Ordinal);

        // The frozen fixture comparison plus the exact identities it does not spell out: every placed
        // unit carries exactly its declared pins, U5 is one component with one declared definition drawn
        // as four symbols on two sheets, and every one of the 222 pins is alone in its native net. Returns
        // the placements and the connectivity KiCad itself reported.
        (List<object> Placements, object Connectivity) RequireCreated(CheckedSchematicState state, string when)
        {
            try { PsuCpuFixture.AssertNative(synchronized, state.Electrical, Stage); }
            catch (AssertFailedException error) { throw new AssertFailedException(when + ": " + error.Message, error); }
            var data = state.Electrical.Hierarchy.Data;
            var symbols = SchematicModelProjection.NativeSymbols(synchronized, data);
            var model = synchronized.Engineering.Circuit;
            var components = model.Components.ToDictionary(c => c.Id);
            var definitions = model.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
            var parts = model.Parts.ToDictionary(p => p.Id);
            var sheetKeys = expected.Sheets.ToDictionary(s => s.ModelSheetInstance, s => s.Key);
            var result = new List<object>();
            foreach (var occurrence in model.Symbols.OrderBy(s => s.Id))
            {
                var component = components[occurrence.ComponentId];
                var part = parts[definitions[component.DefinitionId].PartId];
                var symbol = symbols[occurrence.Id];
                string sheet = sheetKeys[occurrence.EffectiveSheetInstanceId(component)];
                Assert.AreEqual(sheetPaths[sheet], string.Join('/', symbol.Path.Path.Select(p => p.Value)), when + ": " + component.Reference);
                CollectionAssert.AreEqual(part.Pins.Where(p => p.Unit == 0 || p.Unit == occurrence.Unit).Select(p => p.Number).Order(StringComparer.Ordinal).ToArray(),
                    PlacedPins(symbol), $"{when}: {component.Reference} unit {occurrence.Unit} must carry exactly its declared pins.");
                var record = symbol.InstanceRecords.Records.Single();
                Assert.AreEqual((sheetPaths[sheet], component.Reference, occurrence.Unit),
                    (string.Join('/', record.Path.Select(p => p.Value)), record.Reference, record.Unit), when);
                result.Add(new { component.Reference, occurrence.Unit, sheet, native = symbol.Id.Value });
            }
            Assert.AreEqual(expected.Symbols.Count, symbols.Values.Select(s => s.Id.Value).Distinct().Count(), when);

            var u5 = model.Components.Single(c => c.Reference == "U5");
            Assert.AreEqual(PsuCpuIds.Id(0x07, 7), u5.Id);
            var processor = parts[definitions[u5.DefinitionId].PartId];
            var units = model.Symbols.Where(s => s.ComponentId == u5.Id).OrderBy(s => s.Unit).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, units.Select(s => s.Unit).ToArray());
            CollectionAssert.AreEqual(new[] { "CPU", "CPU", "CPU", "CPU_POWER" }, units.Select(s => sheetKeys[s.EffectiveSheetInstanceId(u5)]).ToArray(), when);
            Assert.AreEqual<Guid?>(PsuCpuIds.Id(0x05, 4), units[3].SheetInstanceId);
            var drawn = units.Select(s => symbols[s.Id]).ToArray();
            Assert.AreEqual(4, drawn.Select(s => s.Id.Value).Distinct().Count(), when);
            var declaration = synchronized.PartSymbols!.Single(s => s.PartId == processor.Id);
            foreach (var unit in drawn)
            {
                Assert.AreEqual(declaration.LibraryId, unit.LibraryId, when);
                // The declared cache key is the library identifier, so KiCad keeps no separate alias (saved and reloaded form).
                Assert.AreEqual(declaration.Symbol.CacheKey, unit.LibraryId.LibraryNickname + ":" + unit.LibraryId.EntryName, when);
                Assert.AreEqual("", unit.LibName, when);
                Assert.AreEqual(declaration.Symbol.Definition.Id, unit.Definition.Id, when);
                Assert.AreEqual("U5", unit.ReferenceField.Text.Text_, when);
            }
            CollectionAssert.AreEquivalent(processor.Pins.Select(p => p.Number).ToArray(), drawn.SelectMany(PlacedPins).ToArray(),
                when + ": the four units cover all 177 processor pins exactly once across CPU and CPU_POWER.");
            foreach (string sheet in new[] { "CPU", "CPU_POWER" })
                Assert.IsTrue(SchematicLibraryCacheEquivalence.Equal(declaration.Symbol, data.Instances
                    .Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == sheetPaths[sheet])
                    .CachedSymbols.Single(c => c.CacheKey == declaration.Symbol.CacheKey)), $"{when}: one processor definition on {sheet}");

            var comparison = SchematicElectricalComparison.Compare(synchronized, state.Electrical, [], token);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, when + ": "
                + string.Join("; ", comparison.Differences.Select(d => d.Kind + " " + string.Join(",", d.Pins.Select(p => Reference(p))))));
            var allPins = model.Components.SelectMany(c => parts[definitions[c.DefinitionId].PartId].Pins.Select(p => new PinEndpoint(c.Id, p.Number))).ToArray();
            Assert.HasCount(222, allPins);
            var partitions = comparison.PinPartitions ?? throw new AssertFailedException(when + ": the native pin partition is missing.");
            // Erratum 2026-09-24: the only native net with several pins is the regulator's stacked pair, exactly as the
            // fixture expects it, and no pin of another symbol or another processor unit joins it.
            string[] Keys(IEnumerable<PinEndpoint> pins) => [.. pins.Select(Reference).Order(StringComparer.Ordinal)];
            string Reference(PinEndpoint pin) => components[pin.ComponentId].Reference + "." + pin.Pin;
            var joined = partitions.Where(p => p.Pins.Count > 1).Select(p => string.Join(" ", Keys(p.Pins))).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expected.JoinedPins.Select(g => string.Join(" ", g.Select(p => p.Reference + "." + p.Number).Order(StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal).ToArray(), joined, when + ": only KiCad's stacked U2 pins may share a native net in this stage.");
            CollectionAssert.AreEqual(new[] { "U2.1 U2.4" }, joined, when);
            CollectionAssert.AreEquivalent(allPins, partitions.SelectMany(p => p.Pins).ToArray(), when);
            return (result, new
            {
                nativeNets = state.Electrical.Nets.Count, isolatedPins = partitions.Count(p => p.Pins.Count == 1),
                multiPinNets = partitions.Count(p => p.Pins.Count > 1), pinsWithoutNativeNet = partitions.Count(p => p.SnapshotNetIndex is null),
                joinedNativeNets = partitions.Where(p => p.Pins.Count > 1).Select(p => new { name = p.NativeName, pins = Keys(p.Pins) }).ToArray()
            });
        }

        // Every created symbol's body, pins and visible fields lie inside its sheet's usable region, and no two
        // created symbol bodies on one sheet overlap (fields may overhang; §1.6.3), measured by 2A's
        // NativePresentationChecks in KiCad. Each sheet is also rendered as retained evidence.
        async Task CheckPresentation(CheckedSchematicState state)
        {
            var bindings = synchronized.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
            var names = expected.Symbols.ToDictionary(s => bindings[s.Occurrence], s => s.Reference + " unit " + s.Unit);
            foreach (var sheet in expected.Sheets)
            {
                var screen = state.Electrical.Hierarchy.Data.Instances.Single(s =>
                    string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == sheetPaths[sheet.Key]);
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = screen.Metadata.Document.Clone() }, token);
                var ids = expected.Symbols.Where(s => s.Sheet == sheet.Key).Select(s => bindings[s.Occurrence]).ToArray();
                if (ids.Length > 0)
                {
                    var issues = (await NativePresentationChecks.CheckSymbolPlacementAsync(client, screen.Metadata.Document, ids, usable[sheet.Key], token))
                        .Where(i => i.Code != NativePresentationChecks.SymbolBodiesOverlap || expected.Presentation.SymbolBodiesDisjoint).ToArray();
                    Assert.AreEqual(0, issues.Length, $"{sheet.Key}: " + string.Join("; ",
                        issues.Select(i => i.Code + " " + string.Join(" and ", i.Symbols.Select(id => names[id])))));
                }
                var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = screen.Metadata.Document.Clone() }, token);
                await File.WriteAllBytesAsync(Evidence("render-" + sheet.Key.ToLowerInvariant() + ".png"), image.Preview.Png.ToByteArray(), token);
            }
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        }

        static string[] PlacedPins(SchematicSymbolInstance symbol) => [.. symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
            .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
            .Where(p => p.Pin.LibraryPinId is not null && ((p.Child.Unit?.Unit ?? 0) == 0 || p.Child.Unit!.Unit == symbol.Unit.Unit)
                && ((p.Child.BodyStyle?.Style ?? 0) == 0 || p.Child.BodyStyle!.Style == (symbol.BodyStyle?.Style ?? 1)))
            .Select(p => p.Pin.Number).Order(StringComparer.Ordinal)];
    }

    // A tool result kept as evidence keeps every small field. A long string (such as candidate XML) becomes its
    // length and SHA-256, and a long list of strings (such as native operations) its count and SHA-256, so a
    // proof's evidence stays well inside its retained-artifact cap. The duplicated text content block is dropped.
    private static string RetainedToolEvidence(JsonElement result) => new JsonObject
    {
        ["isError"] = result.TryGetProperty("isError", out var error) && error.GetBoolean(),
        ["structuredContent"] = result.TryGetProperty("structuredContent", out var content) ? RetainedJson(content) : null
    }.ToJsonString();

    private static JsonNode? RetainedJson(JsonElement element)
    {
        const int Limit = 4096;
        static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String when element.GetString()!.Length > Limit:
                return new JsonObject { ["length"] = element.GetString()!.Length, ["sha256"] = Hash(element.GetString()!) };
            case JsonValueKind.Array when element.GetRawText().Length > Limit && element.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String):
                return new JsonObject { ["count"] = element.GetArrayLength(),
                    ["sha256"] = Hash(string.Join('\n', element.EnumerateArray().Select(e => e.GetString()))) };
            case JsonValueKind.Object:
                var retained = new JsonObject();
                foreach (var property in element.EnumerateObject()) retained[property.Name] = RetainedJson(property.Value);
                return retained;
            case JsonValueKind.Array:
                return new JsonArray([.. element.EnumerateArray().Select(RetainedJson)]);
            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }

    private static async Task VerifyXmlComponentCreation(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, bool interruptAfterNativeEdit, CancellationToken token)
    {
        string path = Path.Combine(evidence, instanceId + "-created-design.xml");
        var store = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-creation-recovery.json"));
        var initial = await Capture();
        var baseline = ProbeElectricalModel(initial.Electrical);
        byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(path, bytes, token);
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId),
            new(initial.State.Revision.Epoch, initial.State.Revision.Sequence), initial.Electrical.Hierarchy.TrackingComplete,
            baseline, bytes, baseline.Schematic.Clone(), [], BaselineElectrical: initial.Electrical.Clone(),
            ObservedElectrical: initial.Electrical.Clone()), null);
        await SchematicSynchronizationExecutor.ApplyAsync(store, client, path, saved.RevisionToken, Guid.NewGuid(), token);
        baseline = store.Read()!.State.Baseline;
        var createdIds = new List<Guid>();
        var newDefinitions = new Dictionary<Guid, ComponentDefinition>();
        var components = baseline.Engineering.Circuit.Components.ToList();
        var occurrences = baseline.Engineering.Circuit.Symbols.ToList();
        var part = baseline.Engineering.Circuit.Parts.Single();
        SchematicPartSymbol? declaration = null;
        if (interruptAfterNativeEdit)
        {
            // One editor exercises the existing-template path; the other uses a new
            // explicitly owned definition that has never had a placed component.
            var sourceOccurrence = baseline.Engineering.Circuit.Symbols.OrderBy(s => s.Id).First();
            var sourceSymbol = SchematicModelProjection.NativeSymbols(baseline, baseline.Schematic)[sourceOccurrence.Id];
            var sourceLink = sourceSymbol.LibraryId ?? sourceSymbol.Definition.Id;
            string sourceKey = sourceSymbol.LibName.Length != 0 ? sourceSymbol.LibName
                : (sourceLink.LibraryNickname.Length == 0 ? "" : sourceLink.LibraryNickname + ":") + sourceLink.EntryName;
            var original = baseline.Schematic.Instances.Single(s => s.Metadata.Document.SheetPath.Equals(sourceSymbol.Path))
                .CachedSymbols.Single(s => s.CacheKey == sourceKey).Clone();
            original.CacheKey = "Automation:DeclaredProbe";
            original.Definition.Id = new() { LibraryNickname = "Owned", EntryName = "DeclaredProbeDefinition" };
            original.Definition.Keywords = "independent declared-part creation fixture";
            // Three units: units 1 and 2 stay on the repeated channel sheets, where each must remain one
            // symbol shared by both channels, and only unit 3 moves to the root sheet (ledger p8a7fc65faff17c1b).
            original.Definition.UnitCount = 3;
            original.Definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Detailed" });
            foreach (var child in original.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                Assert.IsNull(pin.LibraryPinId);
                pin.Id.Value = Guid.NewGuid().ToString("D");
                child.Unit = new() { Unit = 1 };
                child.BodyStyle = new() { Style = 2 };
                child.Item = Any.Pack(pin);
            }
            var extraChild = original.Definition.Items.First(c => c.Item.Is(SchematicPin.Descriptor)).Clone();
            extraChild.Unit = new() { Unit = 2 };
            var extra = extraChild.Item.Unpack<SchematicPin>();
            extra.Id.Value = Guid.NewGuid().ToString("D"); extra.Number += "_extra"; extra.Name = "Extra_declared_pin";
            extra.Position.YNm += 2_540_000; extraChild.Item = Any.Pack(extra);
            original.Definition.Items.Add(extraChild);
            var thirdChild = original.Definition.Items.First(c => c.Item.Is(SchematicPin.Descriptor)).Clone();
            thirdChild.Unit = new() { Unit = 3 };
            var third = thirdChild.Item.Unpack<SchematicPin>();
            third.Id.Value = Guid.NewGuid().ToString("D"); third.Number += "_unit3"; third.Name = "Unit3_declared_pin";
            third.Position.YNm += 5_080_000; thirdChild.Item = Any.Pack(third);
            original.Definition.Items.Add(thirdChild);
            var inactiveChild = original.Definition.Items.First(c => c.Item.Is(SchematicPin.Descriptor)).Clone();
            inactiveChild.BodyStyle = new() { Style = 1 };
            var inactivePin = inactiveChild.Item.Unpack<SchematicPin>();
            inactivePin.Id.Value = Guid.NewGuid().ToString("D");
            inactiveChild.Item = Any.Pack(inactivePin); original.Definition.Items.Add(inactiveChild);
            original.Definition.ValueField.CustomProperties.Add(new CustomProperty { Key = "automation.source", Value = "fixture-datasheet:page-2" });
            var manufacturer = original.Definition.DescriptionField.Clone();
            manufacturer.Name = "Manufacturer"; manufacturer.Text.Text_ = "Declared fixture";
            manufacturer.CustomProperties.Add(new CustomProperty { Key = "automation.guidance", Value = "Keep this field-owned instruction." });
            original.Definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(manufacturer),
                IsPrivate = manufacturer.IsPrivate });
            part = part with { Id = Guid.NewGuid(), Name = "Explicit declared three-unit probe", Units = 3,
                Pins = [.. part.Pins.Select(p => p with { Unit = 1 }), new(extra.Number, extra.Name, 2), new(third.Number, third.Name, 3)] };
            declaration = new(part.Id, new() { LibraryNickname = "Declared", EntryName = "ProbeSource" }, original, BodyStyle: 2);
        }
        int designator = 801;
        var rootInstance = baseline.Engineering.Circuit.SheetInstances.Single(s => s.ParentId is null);
        foreach (var sheet in baseline.Engineering.Circuit.SheetInstances.OrderBy(s => s.Id))
        {
            if (!newDefinitions.TryGetValue(sheet.DefinitionId, out var definition))
                newDefinitions.Add(sheet.DefinitionId, definition = new(Guid.NewGuid(), part.Id, "XML-created probe"));
            Guid id = Guid.NewGuid(); createdIds.Add(id);
            components.Add(new(id, definition.Id, sheet.Id, "TP" + designator++));
            // Declared multi-unit parts owned by the repeated channel sheets place their last unit
            // on the root sheet: one component identity whose units sit on different sheets.
            occurrences.AddRange(Enumerable.Range(1, part.Units).Select(unit => new SymbolOccurrence(Guid.NewGuid(), id, unit, null,
                declaration is not null && unit == part.Units && sheet.Id != rootInstance.Id ? rootInstance.Id : null)));
        }
        var crossSheet = occurrences.Where(s => s.SheetInstanceId is not null && createdIds.Contains(s.ComponentId)).ToArray();
        Assert.AreEqual(declaration is null ? 0 : baseline.Engineering.Circuit.SheetInstances.Count - 1, crossSheet.Length);
        var desired = baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
        {
            Parts = declaration is null ? baseline.Engineering.Circuit.Parts : [.. baseline.Engineering.Circuit.Parts, part],
            Sheets = baseline.Engineering.Circuit.Sheets.Select(s => s with
                { Components = [.. s.Components, newDefinitions[s.Id]] }).ToArray(),
            Components = components, Symbols = occurrences
        } }, PartSymbols = declaration is null ? null : [declaration] };
        bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
        await File.WriteAllBytesAsync(path, bytes, token);
        saved = store.Read()!; saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
        var layoutBefore = await Capture();
        var regions = new List<SchematicLayoutRegion>();
        foreach (var screen in baseline.Schematic.Instances.DistinctBy(s => s.Metadata.ScreenId.Value))
        {
            var page = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            { Document = screen.Metadata.Document.Clone(), ExpectedRevision = layoutBefore.State.Revision.Clone() }, token);
            // Explicit fixture policy: reserve the bottom 50 mm for the drawing
            // sheet/title block. Production callers must supply their own actual
            // page constraints; this is not an automatic title-block detector.
            regions.Add(new(Guid.Parse(page.ScreenId.Value), new(page.PageBounds.Position.XNm + 10_000_000,
                page.PageBounds.Position.YNm + 10_000_000, page.PageBounds.Position.XNm + page.PageBounds.Size.XNm - 10_000_000,
                page.PageBounds.Position.YNm + page.PageBounds.Size.YNm - 50_000_000), []));
        }
        JsonElement initialLayout;
        // The codes the public tools actually returned for the inconsistent declaration.
        object? crossSheetRejection = null;
        await using (var layoutHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-layout-host"), Path.Combine(evidence, instanceId + "-layout-host.log"), token))
        {
            RequireToolSuccess(await layoutHost.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var layoutArgs = new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = saved.RevisionToken,
                gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L, regions,
                userInstructions = "Keep the new test-point references visible and preserve existing work." };
            var staleLayout = await layoutHost.Tool("kicad_design_propose_initial_layout", layoutArgs with { expectedRevisionToken = "stale" });
            Assert.IsTrue(staleLayout.GetProperty("isError").GetBoolean());
            var wrongLayout = await layoutHost.Tool("kicad_design_propose_initial_layout", layoutArgs with { instanceId = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(wrongLayout.GetProperty("isError").GetBoolean());
            var smallRegions = regions.Select(r => r with { UsableBounds = new(0, 0, 100, 100) }).ToList();
            var noSpace = await layoutHost.Tool("kicad_design_propose_initial_layout", layoutArgs with { regions = smallRegions });
            RequireToolSuccess(noSpace);
            Assert.IsFalse(noSpace.GetProperty("structuredContent").GetProperty("canPropose").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, noSpace.GetProperty("structuredContent").GetProperty("desiredXml").ValueKind);
            var invalidGrid = await layoutHost.Tool("kicad_design_propose_initial_layout", layoutArgs with { gridNm = 101L });
            Assert.IsTrue(invalidGrid.GetProperty("isError").GetBoolean());
            var reply = await layoutHost.Tool("kicad_design_propose_initial_layout", layoutArgs);
            RequireToolSuccess(reply); initialLayout = reply.GetProperty("structuredContent").Clone();
            Assert.AreEqual(layoutBefore, await Capture(), "Initial placement preparation must not change the native document.");
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
            Assert.IsTrue(initialLayout.GetProperty("canPropose").GetBoolean(), initialLayout.GetRawText());
            if (declaration is not null)
                saved = await RejectPartialRepeatedSheetUnit(layoutHost, layoutArgs.regions, layoutArgs.userInstructions,
                    SchematicDesignXml.Read(initialLayout.GetProperty("desiredXml").GetString()!, []));
        }
        Assert.IsTrue(initialLayout.GetProperty("canPropose").GetBoolean(), initialLayout.GetRawText());
        Assert.AreNotEqual(JsonValueKind.Null, initialLayout.GetProperty("refinement").ValueKind);
        Assert.IsTrue(initialLayout.GetProperty("requiresVisualReview").GetBoolean());
        Assert.IsTrue(initialLayout.GetProperty("requiresNativeConnectivityValidation").GetBoolean());
        var actualScope = initialLayout.GetProperty("refinement").GetProperty("AffectedSymbols").EnumerateArray()
            .Select(s => Guid.Parse(s.GetString()!)).ToArray();
        var baselineOccurrences = baseline.Engineering.Circuit.Symbols.Select(s => s.Id).ToHashSet();
        CollectionAssert.AreEquivalent(occurrences.Where(s => !baselineOccurrences.Contains(s.Id)).Select(s => s.Id).ToArray(), actualScope,
            "Native symbols without optional XML coordinates are existing work, not permission to rearrange them.");
        Assert.AreEqual(layoutBefore, await Capture(), "Initial placement preparation must not change the native document.");
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
        var layoutPlaced = SchematicDesignXml.Read(initialLayout.GetProperty("desiredXml").GetString()!, []).Engineering.Circuit.Symbols
            .ToDictionary(s => s.Id);
        foreach (var unit in crossSheet)
            Assert.AreEqual(unit with { Placement = layoutPlaced[unit.Id].Placement }, layoutPlaced[unit.Id],
                "Initial placement must keep a unit on the sheet its occurrence names.");
        bytes = Encoding.UTF8.GetBytes(initialLayout.GetProperty("desiredXml").GetString()!);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-initial-layout.json"),
            initialLayout.GetRawText(), token);
        await File.WriteAllBytesAsync(path, bytes, token);
        saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
        var plan = SchematicSynchronizationPlanner.Plan(saved.State, token);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-creation-planned.xml"), plan.CandidateXml, token);
        var before = await Capture();
        var geometry = await MeasureCreationCandidates(client, document, before, plan.Candidate!, baseline,
            evidence, instanceId, token);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => SchematicSynchronizationExecutor.ApplyAsync(
                store, client, path, saved.RevisionToken, Guid.NewGuid(), cancelled.Token));
        }
        Assert.AreEqual(before, await Capture()); Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        Guid operation = Guid.NewGuid();
        var args = new { instanceId, recoveryPath = store.StatePath, designPath = path,
            expectedRevisionToken = saved.RevisionToken, operationId = operation.ToString("D") };
        string? interruptedNativeOperation = null;
        DocumentRevision? interruptedNativeRevision = null;
        if (interruptAfterNativeEdit)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(30));
            string marker = Path.Combine(evidence, instanceId + "-creation-pause.json");
            await using var pausedHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo("native-edit", marker),
                Path.Combine(evidence, instanceId + "-creation-host"), Path.Combine(evidence, instanceId + "-creation-paused-host.log"), limit.Token);
            RequireToolSuccess(await pausedHost.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            Task ready = SyncHarnessProcessTests.WaitForMarkerAsync(marker, limit.Token);
            Task<JsonElement> call = pausedHost.Tool("kicad_design_sync_apply", args);
            try
            {
                Task reached = await Task.WhenAny(ready, call);
                if (reached == call) { RequireToolSuccess(await call); Assert.Fail("The creation did not reach the interruption checkpoint."); }
                await ready;
                var pending = store.Read()!.State;
                var nativeReceipt = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
                {
                    Document = document.Clone(), ProcessEpoch = client.Epoch,
                    OperationId = pending.PendingMutation!.OperationId,
                    ExpectedRequest = new() { Batch = pending.PendingMutation.Clone(), ExpectedState = pending.PendingNativeState!.Clone() }
                }, limit.Token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-creation-native-receipt.json"),
                    SchematicJson.Formatter.Format(nativeReceipt), limit.Token);
                Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, nativeReceipt.Status,
                    nativeReceipt.ErrorCode + ": " + nativeReceipt.ErrorMessage);
                var edited = await Capture(); Assert.IsTrue(edited.State.NativeContentDirty);
                interruptedNativeOperation = store.Read()!.State.PendingMutation!.OperationId;
                interruptedNativeRevision = new(edited.State.Revision.Epoch, edited.State.Revision.Sequence);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, limit.Token));
                await pausedHost.TerminateAsync(); Assert.IsTrue(pausedHost.ForcedTermination);
                await Assert.ThrowsAsync<IOException>(async () => { await call; });
                Assert.AreEqual(edited, await Capture(), "Terminating MCP must preserve the dirty independent native editor.");
            }
            catch { await pausedHost.TerminateAsync(); try { await call; } catch (Exception) { } throw; }
        }
        object connectionGate;
        SchematicDesign created;
        await using (var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-creation-host"), Path.Combine(evidence, instanceId + "-creation-host.log"), token))
        {
            RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var beforeStale = await Capture();
            var stale = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath,
                designPath = path, expectedRevisionToken = "not-current", operationId = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(stale.GetProperty("isError").GetBoolean()); Assert.AreEqual(beforeStale, await Capture());
            var result = await host.Tool("kicad_design_sync_apply", args);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-creation-result.json"), result.GetRawText(), token);
            if (result.TryGetProperty("isError", out var error) && error.GetBoolean())
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-creation-actual.xml"),
                    SchematicDataXml.Write((await Capture()).Electrical.Hierarchy.Data), token);
            RequireToolSuccess(result);
            if (interruptAfterNativeEdit)
            {
                Assert.AreEqual(interruptedNativeOperation, store.Read()!.State.LastSynchronization!.Result(store.Read()!.RevisionToken, false).NativeReceipt!.OperationId);
                var recovered = await Capture();
                Assert.AreEqual(interruptedNativeRevision, new DocumentRevision(recovered.State.Revision.Epoch, recovered.State.Revision.Sequence));
            }
            var replay = await host.Tool("kicad_design_sync_apply", args); RequireToolSuccess(replay);
            Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
            await RequireAgreement(createdIds.Count);
            await VerifyCreatedGeometry(client, document, geometry, token);
            created = store.Read()!.State.Baseline;
            var createdBindings = created.SymbolBindings.Where(b => !baseline.SymbolBindings.Any(old => old.SymbolOccurrenceId == b.SymbolOccurrenceId)).ToArray();
            Assert.AreEqual(baseline.Engineering.Circuit.SheetInstances.Count * part.Units, createdBindings.Length);
            // Every unit on its own sheet: one physical symbol per unit and physical screen, shared by
            // repeated instances. With the channel components' last unit on the root sheet instead, the
            // root screen holds all root units plus one separate symbol per moved unit, and the shared
            // channel screen holds only the remaining units.
            int screenCount = baseline.Schematic.Instances.Select(s => s.Metadata.ScreenId.Value).Distinct().Count();
            Assert.AreEqual(crossSheet.Length == 0 ? screenCount * part.Units : part.Units + crossSheet.Length + (part.Units - 1),
                createdBindings.Select(b => b.NativeObjectId).Distinct().Count());
            await VerifyCrossSheetUnits(created);
            var beforeNoOp = await Capture();
            var noOp = await SchematicSynchronizationExecutor.ApplyAsync(store, client, path, store.Read()!.RevisionToken, Guid.NewGuid(), token);
            Assert.IsFalse(noOp.NativeMutationCommitted); Assert.IsFalse(noOp.NativeFilesSaved);
            Assert.AreEqual(beforeNoOp, await Capture());
            // The CN-1 gate plans through this same attached public host rather than starting another one.
            connectionGate = await RequireConnectionRealizationGated(host);
        }
        await using (var automatic = await AutomaticDesignSynchronization.StartAsync(store, client, path, store.Read()!.RevisionToken, token))
        {
            await Watching(createdIds.Count);
            await FocusedSchematicShortcut(client, document, processId, display, "z", token);
            await Watching(0); await RequireAgreement(0);
            await FocusedSchematicShortcut(client, document, processId, display, "y", token);
            await Watching(createdIds.Count); await RequireAgreement(createdIds.Count);
            Assert.AreEqual(SchematicNetReconciliation.Bindings(created), SchematicNetReconciliation.Bindings(store.Read()!.State.Baseline));

            // A saved XML addition must reach KiCad from the file event itself,
            // without a manual refresh, apply call or recovery-store mutation.
            var current = store.Read()!.State.Baseline;
            var rootSheet = current.Engineering.Circuit.SheetInstances.Single(s => s.ParentId is null);
            Guid extraComponent = Guid.NewGuid(), extraDefinition = Guid.NewGuid(), extraSymbol = Guid.NewGuid();
            var extra = current with { Engineering = current.Engineering with { Circuit = current.Engineering.Circuit with
            {
                Components = [.. current.Engineering.Circuit.Components, new(extraComponent, extraDefinition, rootSheet.Id, "TP899")],
                Sheets = current.Engineering.Circuit.Sheets.Select(s => s.Id == rootSheet.DefinitionId
                    ? s with { Components = [.. s.Components, new(extraDefinition, part.Id, "Automatic XML addition")] } : s).ToArray(),
                Symbols = [.. current.Engineering.Circuit.Symbols, .. Enumerable.Range(1, part.Units).Select(unit =>
                    new SymbolOccurrence(unit == 1 ? extraSymbol : Guid.NewGuid(), extraComponent, unit,
                        new(200.66m, 130.81m + (unit - 1) * 15.24m, 0, false, false, false)))]
            } } };
            createdIds.Add(extraComponent);
            await File.WriteAllTextAsync(path, SchematicDesignXml.Write(extra, []), token);
            await Watching(createdIds.Count); await RequireAgreement(createdIds.Count);

            async Task Watching(int count)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var status = automatic.Inspect();
                while (status.Phase != AutomaticDesignPhase.Watching || store.Read()!.State.Baseline.Engineering.Circuit.Components.Count(c => createdIds.Contains(c.Id)) != count)
                {
                    Assert.IsFalse(status.Phase is AutomaticDesignPhase.Paused or AutomaticDesignPhase.Stopped,
                        status.ErrorCode + ": " + status.ErrorMessage);
                    status = await automatic.WaitAsync(status.Sequence, deadline.Token);
                }
            }
        }
        // Save/reload is a native persistence check, not a re-rendered in-memory DTO.
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        await RequireAgreement(createdIds.Count, afterReload: true);
        await using (var publicHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-creation-host"), Path.Combine(evidence, instanceId + "-creation-public-reattach.log"), token))
        {
            RequireToolSuccess(await publicHost.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            var beforeReattach = store.Read()!;
            var reloaded = await Capture();
            var oldRefresh = await publicHost.Tool("kicad_design_recovery_refresh", new
                { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = beforeReattach.RevisionToken });
            Assert.IsTrue(oldRefresh.GetProperty("isError").GetBoolean());
            Assert.AreEqual(beforeReattach.RevisionToken, store.Read()!.RevisionToken);
            var reattach = await publicHost.Tool("kicad_design_recovery_reattach", new { instanceId,
                recoveryPath = store.StatePath, expectedRevisionToken = beforeReattach.RevisionToken,
                expectedDocumentEpoch = reloaded.State.Revision.Epoch });
            RequireToolSuccess(reattach);
            Assert.AreEqual(SchematicDesignXml.Write(beforeReattach.State.Baseline, []), SchematicDesignXml.Write(store.Read()!.State.Baseline, []));
            CollectionAssert.AreEqual(beforeReattach.State.DesiredFileBytes, store.Read()!.State.DesiredFileBytes);
            Assert.AreEqual(reloaded, await Capture());
            var start = await publicHost.Tool("kicad_design_automatic_sync_start", new { instanceId,
                recoveryPath = store.StatePath, designPath = path, expectedRecoveryRevision = store.Read()!.RevisionToken });
            RequireToolSuccess(start);
            string sessionId = start.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
            var status = start.GetProperty("structuredContent").GetProperty("status");
            while (status.GetProperty("phase").GetString() != "Watching")
            {
                Assert.AreNotEqual("Paused", status.GetProperty("phase").GetString(), status.GetRawText());
                var next = await publicHost.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId,
                    afterSequence = status.GetProperty("sequence").GetUInt64() });
                RequireToolSuccess(next); status = next.GetProperty("structuredContent").GetProperty("status");
            }
            await RequireAgreement(createdIds.Count);
            RequireToolSuccess(await publicHost.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId }));
        }
        await VerifyCreatedFieldLayout(client, document, store, path, createdIds, evidence, instanceId, processId, display, token);
        var anchorLabelJoin = await RequireAnchorLabelJoin();
        var unresolvedSymbol = await RequireUnresolvedSymbolMeasured();
        var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document.Clone() }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-creation-render.png"), image.Preview.Png.ToByteArray(), token);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-creation-window.png"), token);
        Assert.AreEqual(declaration is not null, crossSheetRejection is not null, "The declared editor must record its observed cross-sheet rejection.");
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-creation-proof.json"), JsonSerializer.Serialize(new
        { instanceId, createdIds, operation, realStdioApply = true, repeatedScreenSingleCreate = true,
            coordinateFreeInitialPlacement = true, nativeMeasurementReadOnly = true, explicitPageReservations = true,
            nativeUndoRedoAutomaticallyPublished = true, xmlFileEventCreation = true, interruptAfterNativeEdit,
            interruptedNativeOperation, saveReloadVerified = true, publicMcpReattachmentVerified = true,
            exactReplay = true, declaredPartCreation = declaration is not null, declaredUnits = part.Units,
            selectedBodyStyle = declaration?.BodyStyle, crossSheetUnits = crossSheet.Length,
            crossSheetRejection, connectionGate, anchorLabelJoin, unresolvedSymbol,
            crossPlatformReady = false }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

        // A sheet holding a symbol whose library definition KiCad cannot resolve (here its cache entry is missing, as in a
        // project copied without its libraries) is still measured for generated connections: KiCad reports that symbol as
        // an obstacle of its own drawn bounds, exactly the bounds KiCad itself gives the item, with its pins incomplete
        // (SPGIR_DEFINITION_UNRESOLVED), and every other symbol on the sheet as before. Earlier the whole sheet was refused.
        // The root sheet file is saved, given such a symbol on disk, reloaded, measured, then restored and reloaded again.
        async Task<object> RequireUnresolvedSymbolMeasured()
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
            var clean = await Capture();
            string rootFile = Path.Combine(document.Project.Path, document.Project.Name + ".kicad_sch");
            byte[] original = await File.ReadAllBytesAsync(rootFile, token);
            string text = Encoding.UTF8.GetString(original);
            string unresolvedId = Guid.NewGuid().ToString("D");
            string rootPath = "/" + document.SheetPath.Path[0].Value;
            string symbol = $$"""
                (symbol (lib_id "Unavailable:Unresolved") (at 266.7 25.4 0) (unit 1) (exclude_from_sim no) (in_bom yes) (on_board yes) (dnp no)
                  (uuid "{{unresolvedId}}")
                  (property "Reference" "X1" (at 266.7 20.32 0) (effects (font (size 1.27 1.27))))
                  (property "Value" "Unresolved" (at 266.7 30.48 0) (effects (font (size 1.27 1.27))))
                  (instances (project "{{document.Project.Name}}" (path "{{rootPath}}" (reference "X1") (unit 1)))))

                """;
            int at = text.IndexOf("(sheet_instances", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, at, "The saved root sheet lists its sheet instances.");
            Assert.IsFalse(text.Contains("\"Unavailable:Unresolved\"", StringComparison.Ordinal));
            await File.WriteAllTextAsync(rootFile, text.Insert(at, symbol), new UTF8Encoding(false), token);
            try
            {
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
                var loaded = await Capture();
                var rootDocument = loaded.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1).Metadata.Document;
                var request = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = loaded.State.Revision.Clone() };
                var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-measure-unresolved-symbol.json"), SchematicJson.Formatter.Format(measured), token);
                Assert.AreEqual(loaded.State.Revision, measured.Revision);
                Assert.IsTrue(measured.PinGeometryAvailable);
                var unresolved = measured.Obstacles.Single(o => o.Id.Value == unresolvedId);
                Assert.IsNotNull(unresolved.SymbolPins, "An unresolved symbol is still reported as a symbol.");
                Assert.IsFalse(unresolved.SymbolPins.Complete);
                Assert.AreEqual(SchematicPinGeometryIncompleteReason.SpgirDefinitionUnresolved, unresolved.SymbolPins.IncompleteReason);
                Assert.IsEmpty(unresolved.SymbolPins.Pins);
                Assert.IsTrue(unresolved.Bounds.Size.XNm > 0 && unresolved.Bounds.Size.YNm > 0);
                // Exactly the bounds KiCad itself gives that item on the displayed sheet.
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = rootDocument.Clone() }, token);
                var query = new GetBoundingBox { Header = new() { Document = rootDocument.Clone() }, Mode = BoundingBoxMode.BbmItemAndChildText };
                query.Items.Add(unresolved.Id.Clone());
                var own = await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(query, token);
                Assert.AreEqual(own.Boxes.Single(), unresolved.Bounds, "The unresolved symbol is measured by its own drawn bounds.");
                // Every other item of the sheet is measured as before, every other symbol with its complete pins.
                var others = measured.Obstacles.Where(o => o.Id.Value != unresolvedId).ToArray();
                CollectionAssert.AreEquivalent(SchematicItemDelta.Index(loaded.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(rootDocument)).Items)
                    .Where(p => p.Value is not Group).Select(p => p.Key.ToString("D")).ToArray(), measured.Obstacles.Select(o => o.Id.Value).ToArray());
                Assert.IsTrue(others.Where(o => o.SymbolPins is not null).All(o => o.SymbolPins.Complete), "Resolved symbols keep their exact pins.");
                var planning = await PlanBesideUnresolvedSymbol(clean, loaded, unresolvedId);
                Assert.AreEqual(loaded, await Capture(), "Planning beside the unresolved symbol changes nothing in KiCad.");
                return new { unresolvedSymbol = unresolvedId, measuredWithoutRefusal = true, reason = unresolved.SymbolPins.IncompleteReason.ToString(),
                    ownBoundsMatchKiCad = true, otherSymbols = others.Count(o => o.SymbolPins is not null), planning };
            }
            finally
            {
                await File.WriteAllBytesAsync(rootFile, original, CancellationToken.None);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, CancellationToken.None);
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, CancellationToken.None);
            }
        }

        // XML connections planned on the reloaded root sheet while it holds the symbol KiCad cannot resolve, as an editor that
        // advertises schematic.connection-realization.v1 would plan them. The realizer would keep that symbol's measured
        // bounds clear, but no XML connection reaches the realizer while such a symbol is on a sheet: KiCad captures it with
        // no definition and no pins of its own, so the design can neither own it nor match its pins, and planning refuses
        // before anything is measured. (1) TP899's probe pin joins the probe link: planned into a realization on the same
        // editor state without the symbol, refused with it. (2) A net names the unresolved symbol's own pin, for a design
        // that owned the symbol before its library went missing: refused as well. Nothing is saved, published or sent, and
        // each plan's code and reason are kept as evidence.
        async Task<object> PlanBesideUnresolvedSymbol(CheckedSchematicState clean, CheckedSchematicState loaded, string unresolvedId)
        {
            var session = await client.HandshakeAsync(token);
            var advertised = session.Clone(); advertised.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
            var current = store.Read()!;
            Assert.IsFalse(current.State.HasPendingWork);
            string revisionBefore = current.RevisionToken;
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            // The record a synchronized editor holds for a captured state: its model, KiCad's sheets.
            DesignRecoveryState Adopt(CheckedSchematicState native, SchematicDesign model)
            {
                var adoptedDesign = model with { Schematic = native.Electrical.Hierarchy.Data.Clone() };
                return current.State with { NativeRevision = new(native.State.Revision.Epoch, native.State.Revision.Sequence),
                    TrackingComplete = native.Electrical.Hierarchy.TrackingComplete, Baseline = adoptedDesign,
                    DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(adoptedDesign, [])), Observed = native.Electrical.Hierarchy.Data.Clone(),
                    BaselineElectrical = native.Electrical.Clone(), ObservedElectrical = native.Electrical.Clone(), LastSynchronization = null };
            }
            static DesignRecoveryState Revise(DesignRecoveryState adopted, Func<Circuit, Circuit> edit)
            {
                var revised = adopted.Baseline with { Engineering = adopted.Baseline.Engineering with { Circuit = edit(adopted.Baseline.Engineering.Circuit) } };
                return adopted with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(revised, [])) };
            }
            var model = current.State.Baseline;
            var circuit = model.Engineering.Circuit;
            var link = circuit.Nets.Single(n => n.Pins.Count == 2 && n.Pins.All(p => !createdIds.Contains(p.ComponentId)));
            var drawnPin = part.Pins.Where(p => p.Unit is 0 or 1).OrderBy(p => p.Number, StringComparer.Ordinal).First();
            var tp899 = circuit.Components.Single(c => c.Reference == "TP899");
            Circuit Joined(Circuit c) => c with { Nets = [.. c.Nets.Select(n => n.Id == link.Id ? n with { Pins = [.. n.Pins, new PinEndpoint(tp899.Id, drawnPin.Number)] } : n)] };
            string unresolvedName = "symbol X1 ('Unavailable:Unresolved')";

            // Guard: on this editor's own state without the unresolved symbol, the same revision plans into a realization.
            var guard = SchematicSynchronizationPlanner.Plan(Revise(Adopt(clean, model), Joined), advertised, token);
            var guardIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(guard);
            Assert.AreEqual(new PinEndpoint(tp899.Id, drawnPin.Number),
                guardIntent.Screens.SelectMany(s => s.Islands).SelectMany(i => i.Members).Single(m => m.RequiresStub).Pin.Endpoint);
            // (1) With the symbol on the sheet the same revision is refused before anything is measured, and says why.
            var beside = SchematicSynchronizationPlanner.Plan(Revise(Adopt(loaded, model), Joined), advertised, token);
            Assert.AreEqual(SchematicConnectionErrors.UnalignedElectricalBaseline, beside.ErrorCode, beside.ErrorMessage);
            StringAssert.Contains(beside.ErrorMessage, "KiCad cannot resolve the library definition of " + unresolvedName);
            Assert.IsNull(beside.Candidate); Assert.IsNull(beside.Connections); Assert.IsEmpty(beside.NativeOperations);

            // (2) A design that owned X1 (one part pin, bound to KiCad's symbol) before its library went missing names X1's
            // pin in a new net with TP899's probe pin.
            var rootSheet = circuit.SheetInstances.Single(s => s.ParentId is null);
            Guid x1Part = Guid.NewGuid(), x1Definition = Guid.NewGuid(), x1Component = Guid.NewGuid(), x1Occurrence = Guid.NewGuid();
            var owning = model with { Engineering = model.Engineering with { Circuit = circuit with
                {
                    Parts = [.. circuit.Parts, new(x1Part, "Unresolved part", 1, [new("1", "1", 1)])],
                    Sheets = circuit.Sheets.Select(s => s.Id == rootSheet.DefinitionId
                        ? s with { Components = [.. s.Components, new(x1Definition, x1Part, "Unresolved")] } : s).ToArray(),
                    Components = [.. circuit.Components, new(x1Component, x1Definition, rootSheet.Id, "X1")],
                    Symbols = [.. circuit.Symbols, new SymbolOccurrence(x1Occurrence, x1Component, 1, null)]
                } }, SymbolBindings = [.. model.SymbolBindings, new(x1Occurrence, Guid.Parse(unresolvedId))] };
            var naming = SchematicSynchronizationPlanner.Plan(Revise(Adopt(loaded, owning), c => c with
                { Nets = [.. c.Nets, new CircuitNet(Guid.NewGuid(), "X1_PROBE", [new(x1Component, "1"), new(tp899.Id, drawnPin.Number)])] }), advertised, token);
            Assert.AreEqual(SchematicConnectionErrors.UnalignedElectricalBaseline, naming.ErrorCode, naming.ErrorMessage);
            StringAssert.Contains(naming.ErrorMessage, "KiCad cannot resolve the library definition of " + unresolvedName);
            Assert.IsNull(naming.Candidate); Assert.IsNull(naming.Connections); Assert.IsEmpty(naming.NativeOperations);

            Assert.AreEqual(revisionBefore, store.Read()!.RevisionToken, "Planning must not advance recovery.");
            CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Planning must not publish XML.");
            var result = new
            {
                withoutUnresolvedSymbol = new { canPrepare = guard.CanPrepare, realizes = guard.Connections is not null,
                    joinedPin = new { component = tp899.Id, pin = drawnPin.Number } },
                connectionBeside = new { errorCode = beside.ErrorCode, errorMessage = beside.ErrorMessage },
                netNamingUnresolvedPin = new { errorCode = naming.ErrorCode, errorMessage = naming.ErrorMessage }
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-plan-beside-unresolved-symbol.json"), JsonSerializer.Serialize(result), token);
            return result;
        }

        // CN-1 §6.3 (a) on this editor: a connection KiCad already shows without a label is named where it is. The editor
        // first reloads its saved sheets. In one native step, TP1 and TP2 move together to free room if they must and the
        // probe link is redrawn without its SIGNAL label as one wire that leaves each probe pin straight in the direction a
        // stub from it would take, so no join stub has room on either pin (it would run along that wire) and the realizer
        // must fall back to a label on a pin itself. The scene does not depend on the identities a run generates. The
        // realizer tries the link's pins in component order (§5.4): the wire turns one grid in front of the first pin, where
        // a label on that pin would cross it, and runs six grids straight out of the second, so the label on the first pin is
        // refused for exactly that turn and the second pin is named. The XML revision names the link LINK and adds a new
        // probe TP900 to it, declared with its own copy of the probe definition so that its fields come from that definition
        // and not from whichever placed probe a run's identities make the planner copy (in governed run
        // t20260924T114705Z-b90471 that copy carried a reference field dragged 122 mm away, so TP900's own measured bounds
        // covered its whole stub room and the realizer rightly refused it, §6.4 rule 6). TP900 is measured live first,
        // turned and placed where KiCad's own measurement leaves room for its stub and label, and the realizer must draw
        // exactly the predicted stub and add exactly TP900's definition to the sheet's library cache. The realization is
        // measured live and not applied (lane 2C's native
        // assertion is still missing), kept as the join-turned-link replay fixture, and the redraw is undone natively.
        async Task<object> RequireAnchorLabelJoin()
        {
            var session = await client.HandshakeAsync(token);
            var advertised = session.Clone(); advertised.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
            var current = store.Read()!;
            Assert.IsFalse(current.State.HasPendingWork);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
            var before = await Capture();
            Assert.IsFalse(before.State.NativeContentDirty);
            var data = before.Electrical.Hierarchy.Data;
            var rootScreen = data.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1);
            var rootDocument = rootScreen.Metadata.Document;
            var policy = SchematicConnectionPolicy.FromSnapshot(data);
            long grid = policy.GridNm;
            var circuit = current.State.Baseline.Engineering.Circuit;
            var link = circuit.Nets.Single(n => n.Pins.Count == 2 && n.Pins.All(p => !createdIds.Contains(p.ComponentId)));
            // The link's pins in the order the realizer tries them as join candidates: island member order, by component.
            var tried = link.Pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).ToArray();
            var keys = SchematicConnectionIntentBuilderTests.Keys(current.State.Baseline, [.. tried.Select(p => (p.ComponentId, p.Pin))]);
            var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = rootDocument.Clone(), ExpectedRevision = before.State.Revision.Clone() }, token);
            // probes[0] is tried first and must be refused; probes[1] is named.
            var probes = keys.Select(k => measured.Obstacles.Where(o => o.SymbolPins is not null)
                .SelectMany(o => o.SymbolPins.Pins.Select(p => (Owner: o, Pin: p))).Single(x => x.Pin.Id.Value == k.PlacedPinId.ToString("D"))).ToArray();
            var outward = SchematicConnectionGeometry.Outward(probes[0].Pin);
            Assert.AreEqual(outward, SchematicConnectionGeometry.Outward(probes[1].Pin), "Both fixture probes face the same way.");
            long Along(Vector2 v) => outward.Dx * v.XNm + outward.Dy * v.YNm;
            Assert.AreEqual(Along(probes[0].Pin.Position), Along(probes[1].Pin.Position), "Both fixture probe pins lie on one line.");
            var oldWire = rootScreen.Items.Where(i => i.Is(SchematicLine.Descriptor)).Select(i => i.Unpack<SchematicLine>()).Single();
            var oldLabel = rootScreen.Items.Where(i => i.Is(LocalLabel.Descriptor)).Select(i => i.Unpack<LocalLabel>()).Single(l => l.Text.Text_ == "SIGNAL");
            // Free room: a rectangle keeping a clearance from everything KiCad measured on the root sheet but the listed items,
            // inside the fixture's usable region (10 mm page inset, bottom 50 mm kept for the title block).
            static (long L, long T, long R, long B) Rect(Box2 b) => (b.Position.XNm, b.Position.YNm, b.Position.XNm + b.Size.XNm, b.Position.YNm + b.Size.YNm);
            bool Free((long L, long T, long R, long B) area, ISet<string> ignored) =>
                area.L >= 10_000_000 && area.T >= 10_000_000 && area.R <= measured.PageBounds.Size.XNm - 10_000_000 && area.B <= measured.PageBounds.Size.YNm - 50_000_000
                && measured.Obstacles.Where(o => !ignored.Contains(o.Id.Value)).All(o =>
                {
                    var box = Rect(o.Bounds);
                    return area.R + policy.ClearanceNm < box.L || area.L - policy.ClearanceNm > box.R || area.B + policy.ClearanceNm < box.T || area.T - policy.ClearanceNm > box.B;
                });
            static bool Apart((long L, long T, long R, long B) a, (long L, long T, long R, long B) b) => a.R < b.L || a.L > b.R || a.B < b.T || a.T > b.B;
            // The scene: TP1 and TP2 (whose model leaves their placement to the editor) keep their relative placement and move
            // together, if they must, to the first spot where both, the wire six grids in front of their pins and room for a
            // label on either pin keep clear of everything else on the root sheet. Fields dragged far from their symbols by
            // earlier checks make wide bounds elsewhere on this sheet, and a label may not overlap any bounds.
            long depth = 6 * grid;
            var linkItems = new HashSet<string>(StringComparer.Ordinal) { probes[0].Owner.Id.Value, probes[1].Owner.Id.Value, oldWire.Id.Value, oldLabel.Id.Value };
            var (b0, b1) = (Rect(probes[0].Owner.Bounds), Rect(probes[1].Owner.Bounds));
            (long L, long T, long R, long B) Scene(long dx, long dy)
            {
                long reach = depth + 4 * grid, margin = 2 * grid;
                return (Math.Min(b0.L, b1.L) + dx - (outward.Dx < 0 ? reach : 0) - margin, Math.Min(b0.T, b1.T) + dy - (outward.Dy < 0 ? reach : 0) - margin,
                    Math.Max(b0.R, b1.R) + dx + (outward.Dx > 0 ? reach : 0) + margin, Math.Max(b0.B, b1.B) + dy + (outward.Dy > 0 ? reach : 0) + margin);
            }
            const long Step = 5_080_000; // the fixture's placement grid
            var shifts = new List<(long Dx, long Dy)> { (0, 0) };
            for (long y = 6 * Step; y <= 130_000_000; y += Step)
                for (long x = 4 * Step; x <= 260_000_000; x += Step)
                    shifts.Add((x - probes[0].Pin.Position.XNm, y - probes[0].Pin.Position.YNm));
            var shift = shifts.Cast<(long Dx, long Dy)?>().FirstOrDefault(s => Free(Scene(s!.Value.Dx, s.Value.Dy), linkItems))
                ?? throw new AssertFailedException("The root sheet has no free room for the redrawn probe link.");
            var scene = Scene(shift.Dx, shift.Dy);
            Vector2 Moved(Vector2 v) => new() { XNm = v.XNm + shift.Dx, YNm = v.YNm + shift.Dy };
            Vector2 Out(Vector2 v, long length) => new() { XNm = v.XNm + outward.Dx * length, YNm = v.YNm + outward.Dy * length };
            var (p0, p1) = (Moved(probes[0].Pin.Position), Moved(probes[1].Pin.Position));
            var side = (Dx: Math.Sign(p1.XNm - p0.XNm), Dy: Math.Sign(p1.YNm - p0.YNm));
            Assert.AreEqual(1, Math.Abs(side.Dx) + Math.Abs(side.Dy), "The probe pins lie side by side on one axis.");
            long apart = Math.Abs(p1.XNm - p0.XNm) + Math.Abs(p1.YNm - p0.YNm);
            // The turn runs across to a whole number of grids from the first pin, at least three grids from either pin.
            long bend = apart / 2 / grid * grid;
            Assert.IsTrue(bend >= 3 * grid && apart - bend >= 3 * grid, "The probe pins are far enough apart for the turn.");
            Vector2 Across(Vector2 v, long length) => new() { XNm = v.XNm + side.Dx * length, YNm = v.YNm + side.Dy * length };
            var path = new[] { p0, Out(p0, grid), Out(Across(p0, bend), grid), Out(Across(p0, bend), depth), Out(p1, depth), p1 };

            SchematicSymbolInstance Shifted(string id)
            {
                var symbol = rootScreen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Single(x => x.Id.Value == id).Clone();
                symbol.Position = Moved(symbol.Position);
                foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField, symbol.DatasheetField, symbol.DescriptionField }
                    .Concat(symbol.UserFields).Where(f => f?.Text?.Position is not null))
                    field.Text.Position = Moved(field.Text.Position);
                return symbol;
            }
            var wires = Enumerable.Range(0, path.Length - 1).Select(i => new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = path[i].Clone(),
                End = path[i + 1].Clone(), Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked }).ToArray();
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = before.State.Revision.Epoch,
                ExpectedRevision = before.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Redraw the probe link without its label" };
            if (shift != (0, 0))
                foreach (var owner in probes.Select(p => p.Owner.Id.Value).Distinct())
                    batch.Operations.Add(new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Update = Any.Pack(Shifted(owner)) });
            batch.Operations.Add(new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Remove = oldLabel.Id.Clone() });
            batch.Operations.Add(new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Remove = oldWire.Id.Clone() });
            batch.Operations.Add(wires.Select(w => new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Create = Any.Pack(w) }));
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(new() { Batch = batch, ExpectedState = before.State.Clone() }, token);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorCode + ": " + receipt.ErrorMessage);
            var linked = await Capture();
            // KiCad itself joins exactly the two probes through the new wire, and nothing names that connection.
            var native = linked.Electrical.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == probes[0].Pin.Id.Value));
            CollectionAssert.AreEquivalent(new[] { probes[0].Pin.Id.Value, probes[1].Pin.Id.Value }.Concat(wires.Select(w => w.Id.Value)).ToArray(),
                native.Sheets.SelectMany(s => s.Items).Select(i => i.Value).ToArray(), "The redrawn link joins exactly the two probes.");
            var placed = probes.Select(p => (p.Owner, Pin: new SchematicPinAnchor(p.Pin) { Position = Moved(p.Pin.Position) })).ToArray();

            // The record a synchronized editor holds for this redraw: the same model, KiCad's redrawn sheets. It plans settled.
            var baseline = current.State.Baseline with { Schematic = linked.Electrical.Hierarchy.Data.Clone() };
            var adopted = current.State with { NativeRevision = new(linked.State.Revision.Epoch, linked.State.Revision.Sequence),
                TrackingComplete = linked.Electrical.Hierarchy.TrackingComplete, Baseline = baseline,
                DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, [])), Observed = linked.Electrical.Hierarchy.Data.Clone(),
                BaselineElectrical = linked.Electrical.Clone(), ObservedElectrical = linked.Electrical.Clone(), LastSynchronization = null };
            var settled = SchematicSynchronizationPlanner.Plan(adopted, advertised, token);
            Assert.IsTrue(settled.CanPrepare, settled.ErrorCode + ": " + settled.ErrorMessage);
            Assert.IsEmpty(settled.NativeOperations, "The adopted redraw is settled.");

            // The XML revision names the link LINK and adds the new probe TP900 to it, turned and placed as given. TP900 is a
            // part of its own, declared with a copy of the probe definition KiCad holds for TP1, so it takes its fields from
            // that definition (and the root sheet's library cache gains exactly that definition). Without the declaration
            // the planner would copy the probe with the lowest model occurrence, which a run's identities decide: in governed
            // runs t20260924T114705Z-b90471 and t20260924T124806Z-42fd04 that was the probe whose reference field the
            // manual field check had dragged 60.96 mm aside and 121.666 mm in front of its pin, and KiCad measured such a
            // copy as covering the room in front of its pin in every orientation, so no stub could be drawn.
            var tp1 = rootScreen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Single(x => x.Id.Equals(probes[0].Owner.Id));
            var tp1Library = tp1.LibraryId ?? tp1.Definition.Id;
            string tp1Key = tp1.LibName.Length != 0 ? tp1.LibName
                : (tp1Library.LibraryNickname.Length == 0 ? "" : tp1Library.LibraryNickname + ":") + tp1Library.EntryName;
            var joinSymbol = rootScreen.CachedSymbols.Single(c => c.CacheKey == tp1Key).Clone();
            joinSymbol.CacheKey = "Automation:JoinProbe";
            joinSymbol.Definition.Id = new() { LibraryNickname = "Owned", EntryName = "JoinProbeDefinition" };
            var joinPins = new List<PartPin>();
            foreach (var child in joinSymbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                Assert.IsNull(pin.LibraryPinId);
                pin.Id.Value = Guid.NewGuid().ToString("D");
                child.Unit = new() { Unit = 1 };
                child.Item = Any.Pack(pin);
                joinPins.Add(new(pin.Number, pin.Name, 1));
            }
            var joinPart = new PartDefinition(Guid.NewGuid(), "XML join probe part", 1, joinPins);
            var joinDeclaration = new SchematicPartSymbol(joinPart.Id, new() { LibraryNickname = "Declared", EntryName = "JoinProbe" }, joinSymbol);
            Guid probeId = Guid.NewGuid(), probeDefinition = Guid.NewGuid();
            var probeOccurrences = Enumerable.Range(0, joinPart.Units).Select(_ => Guid.NewGuid()).ToArray();
            var drawnPin = joinPart.Pins.OrderBy(p => p.Number, StringComparer.Ordinal).First();
            var named = link with { Name = "LINK", Pins = [.. link.Pins, new PinEndpoint(probeId, drawnPin.Number)] };
            const long UnitPitch = 15_240_000;
            DesignRecoveryState Revision(long x, long y, int degrees) => adopted with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
                baseline with { PartSymbols = [.. baseline.PartSymbols ?? [], joinDeclaration], Engineering = baseline.Engineering with { Circuit = circuit with
                {
                    Parts = [.. circuit.Parts, joinPart],
                    Components = [.. circuit.Components, new(probeId, probeDefinition, rootInstance.Id, "TP900")],
                    Sheets = circuit.Sheets.Select(s => s.Id == rootInstance.DefinitionId
                        ? s with { Components = [.. s.Components, new(probeDefinition, joinPart.Id, "XML join probe")] } : s).ToArray(),
                    Symbols = [.. circuit.Symbols, .. probeOccurrences.Select((id, i) => new SymbolOccurrence(id, probeId, i + 1,
                        new(x / 1_000_000m, (y + i * UnitPitch) / 1_000_000m, degrees, false, false, false)))],
                    Nets = [.. circuit.Nets.Select(n => n.Id == link.Id ? named : n)]
                } } }, [])) };
            Assert.IsNull(SchematicSynchronizationPlanner.Plan(Revision(30 * Step, 10 * Step, 0), session, token).Connections,
                "This editor's own handshake admits no wiring.");

            // TP900 as the planner creates it, measured live on the redrawn sheet in each orientation, with the label its stub
            // would carry: the first orientation in which some policy stub length keeps that label clear of TP900's own
            // bounds (every unit's) and the stub clear of its other units, and the first spot on the fixture grid where all of
            // TP900 lies in the usable region clear of the redrawn link and its labels, that stub and label keep clear of
            // everything else, and its pins touch nothing. KiCad's placement is translation-invariant, so moving TP900 moves
            // all of this with it.
            var provisional = SchematicSynchronizationPlanner.Plan(Revision(30 * Step, 10 * Step, 0), advertised, token);
            var provisionalIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(provisional);
            var units = provisional.Candidate!.Schematic.Instances.Single(x => x.Metadata.Document.Equals(rootDocument)).Items
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Where(x => provisionalIntent.CreatedSymbolIds.Contains(Guid.Parse(x.Id.Value))).OrderBy(x => x.Unit.Unit).ToArray();
            Assert.HasCount(joinPart.Units, units);
            Assert.AreEqual("Automation:JoinProbe", units[0].LibName, "TP900 is drawn from its own declared definition.");
            var origin = units[0].Position.Clone();
            var addedPin = provisionalIntent.Screens.Single().Islands.Single().Members.Single(m => m.RequiresStub).Pin;
            var revisionModel = new KiCad.Automation.Model.DocumentRevision(linked.State.Revision.Epoch, linked.State.Revision.Sequence);
            var screenId = Guid.Parse(rootScreen.Metadata.ScreenId.Value);
            var envelopes = new Dictionary<SchematicLabelSpinStyle, (long L, long T, long R, long B)>();
            {
                var request = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = linked.State.Revision.Clone() };
                var spins = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(SchematicConnectionGeometry.Spin).ToArray();
                foreach (var spin in spins)
                    request.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Local,
                        SchematicConnectionIdentity.Probe(revisionModel, screenId, LocalLabel.Descriptor, "LINK", spin, SchematicLabelShape.SlshUnknown, probeId, 0),
                        new() { XNm = 40 * Step, YNm = 20 * Step }, "LINK", spin, policy)));
                var labels = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                for (int i = 0; i < spins.Length; i++)
                {
                    var box = Rect(labels.ItemCandidates[i].Bounds);
                    var at = labels.ItemCandidates[i].Anchor;
                    envelopes[spins[i]] = (box.L - at.XNm, box.T - at.YNm, box.R - at.XNm, box.B - at.YNm);
                }
            }
            static bool Meets((long L, long T, long R, long B) open, (long L, long T, long R, long B) closed) =>
                open.L < open.R && open.T < open.B && closed.L < open.R && closed.R > open.L && closed.T < open.B && closed.B > open.T;
            static bool Touches((long L, long T, long R, long B) a, (long L, long T, long R, long B) b) => a.L <= b.R && b.L <= a.R && a.T <= b.B && b.T <= a.B;
            static (long L, long T, long R, long B) Offset((long L, long T, long R, long B) a, long dx, long dy) => (a.L + dx, a.T + dy, a.R + dx, a.B + dy);
            static (long L, long T, long R, long B) Union((long L, long T, long R, long B) a, (long L, long T, long R, long B) b) =>
                (Math.Min(a.L, b.L), Math.Min(a.T, b.T), Math.Max(a.R, b.R), Math.Max(a.B, b.B));
            static (long L, long T, long R, long B) Inflate((long L, long T, long R, long B) a, long c) => (a.L - c, a.T - c, a.R + c, a.B + c);
            (int Degrees, int Multiple, long X, long Y, (long X, long Y) Pin, (long X, long Y) End, (long L, long T, long R, long B) Label,
                (long L, long T, long R, long B)[] Bounds, SchematicSymbolInstance[] Units)? choice = null;
            var tries = new List<object>();
            foreach (int degrees in new[] { 0, 180, 90, 270 })
            {
                var request = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = linked.State.Revision.Clone() };
                var turned = units.Select(u => { var t = u.Clone(); t.Transform = new() { Orientation = (SchematicSymbolOrientation)(degrees / 90 + 1) }; return t; }).ToArray();
                request.Candidates.Add(turned.Select(t => t.Clone()));
                var geometry = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                Assert.AreEqual(measured.Obstacles.Count, geometry.Obstacles.Count - wires.Length + 2, "Measuring TP900 measures the redrawn sheet.");
                var bounds = geometry.Candidates.Select(c => Rect(c.Bounds)).ToArray();
                var pins = geometry.Candidates.SelectMany(c => c.SymbolPins.Pins).ToArray();
                Assert.IsTrue(geometry.Candidates.All(c => c.SymbolPins.Complete), "TP900's pins are measured completely.");
                var pin = pins.Single(p => p.Id.Value == addedPin.PlacedPinId.ToString("D"));
                var o = SchematicConnectionGeometry.Outward(pin);
                var envelope = envelopes[SchematicConnectionGeometry.Spin(o)];
                var owner = geometry.Candidates.Single(c => c.SymbolPins.Pins.Contains(pin));
                int? multiple = null;
                foreach (int m in SchematicConnectionPolicy.StubMultiples)
                {
                    long ex = pin.Position.XNm + o.Dx * m * grid, ey = pin.Position.YNm + o.Dy * m * grid;
                    var labelBox = (ex + envelope.L, ey + envelope.T, ex + envelope.R, ey + envelope.B);
                    var stub = (Math.Min(pin.Position.XNm, ex), Math.Min(pin.Position.YNm, ey), Math.Max(pin.Position.XNm, ex), Math.Max(pin.Position.YNm, ey));
                    bool clear = bounds.All(b => !Meets(labelBox, b))
                        && geometry.Candidates.Where(c => c != owner).All(c => !Touches(stub, Rect(c.Bounds)))
                        && pins.Where(p => p != pin).All(p => !Touches(stub, (p.Position.XNm, p.Position.YNm, p.Position.XNm, p.Position.YNm))
                            && !Touches(labelBox, (p.Position.XNm, p.Position.YNm, p.Position.XNm, p.Position.YNm))
                            && (Math.Abs(p.Position.XNm - ex) > policy.ClearanceNm || Math.Abs(p.Position.YNm - ey) > policy.ClearanceNm));
                    if (clear) { multiple = m; break; }
                }
                tries.Add(new { degrees, outward = new[] { o.Dx, o.Dy }, multiple });
                if (multiple is not { } chosen) continue;
                long dx0 = pin.Position.XNm + o.Dx * chosen * grid, dy0 = pin.Position.YNm + o.Dy * chosen * grid;
                var labelAt = (dx0 + envelope.L, dy0 + envelope.T, dx0 + envelope.R, dy0 + envelope.B);
                // The room out to the longest policy stub and its label, so that every longer stub (which the replay test
                // takes when it blocks the recorded one) has room as well.
                long far = SchematicConnectionPolicy.StubMultiples[^1] * grid;
                long fx0 = pin.Position.XNm + o.Dx * far, fy0 = pin.Position.YNm + o.Dy * far;
                var room = Inflate(Union(Union((Math.Min(pin.Position.XNm, fx0), Math.Min(pin.Position.YNm, fy0), Math.Max(pin.Position.XNm, fx0),
                    Math.Max(pin.Position.YNm, fy0)), labelAt), (fx0 + envelope.L, fy0 + envelope.T, fx0 + envelope.R, fy0 + envelope.B)), policy.ClearanceNm);
                var whole = bounds.Aggregate(Union);
                for (long y = 3 * Step; choice is null && y <= measured.PageBounds.Size.YNm; y += Step)
                    for (long x = 3 * Step; choice is null && x <= measured.PageBounds.Size.XNm; x += Step)
                    {
                        long dx = x - origin.XNm, dy = y - origin.YNm;
                        var moved = Offset(whole, dx, dy);
                        // All of TP900 stays inside the fixture's usable region; only its stub and label room must be clear.
                        if (moved.L < 10_000_000 || moved.T < 10_000_000 || moved.R > measured.PageBounds.Size.XNm - 10_000_000
                            || moved.B > measured.PageBounds.Size.YNm - 50_000_000) continue;
                        if (!Apart(moved, scene) || !Apart(Offset(room, dx, dy), scene) || !Free(Offset(room, dx, dy), linkItems)) continue;
                        if (!pins.All(p => Free((p.Position.XNm + dx, p.Position.YNm + dy, p.Position.XNm + dx, p.Position.YNm + dy), linkItems))) continue;
                        choice = (degrees, chosen, x, y, (pin.Position.XNm + dx, pin.Position.YNm + dy), (dx0 + dx, dy0 + dy), Offset(labelAt, dx, dy),
                            bounds.Select(b => Offset(b, dx, dy)).ToArray(), turned);
                    }
                if (choice is not null) break;
            }
            Assert.IsNotNull(choice, "Some orientation and spot leave TP900 room for its stub: " + JsonSerializer.Serialize(tries));
            var chosenProbe = choice.Value;

            // The revision with TP900 there. The planner creates exactly the measured symbol, moved and turned.
            var revision = Revision(chosenProbe.X, chosenProbe.Y, chosenProbe.Degrees);
            var plan = SchematicSynchronizationPlanner.Plan(revision, advertised, token);
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
            var created = plan.Candidate!.Schematic.Instances.Single(x => x.Metadata.Document.Equals(rootDocument)).Items
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Where(x => intent.CreatedSymbolIds.Contains(Guid.Parse(x.Id.Value))).OrderBy(x => x.Unit.Unit).ToArray();
            Assert.HasCount(joinPart.Units, created);
            for (int i = 0; i < created.Length; i++)
            {
                var expected = chosenProbe.Units[i].Clone();
                long dx = chosenProbe.X - origin.XNm, dy = chosenProbe.Y - origin.YNm;
                expected.Position = new() { XNm = expected.Position.XNm + dx, YNm = expected.Position.YNm + dy };
                foreach (var field in new[] { expected.ReferenceField, expected.ValueField, expected.FootprintField, expected.DatasheetField, expected.DescriptionField }
                    .Concat(expected.UserFields).Where(f => f?.Text?.Position is not null))
                    field.Text.Position = new() { XNm = field.Text.Position.XNm + dx, YNm = field.Text.Position.YNm + dy };
                Assert.AreEqual(expected, created[i], "TP900 unit " + (i + 1) + " is the measured symbol, moved and turned.");
            }
            var island = intent.Screens.Single().Islands.Single();
            Assert.AreEqual("LINK", island.LabelText);
            Assert.IsFalse(island.AnchorHasMatchingDriver, "Nothing names the redrawn link.");
            Assert.IsTrue(island.JoinRequired);
            CollectionAssert.AreEqual(keys.Select(k => k.PlacedPinId).ToArray(), island.JoinCandidates.Select(p => p.PlacedPinId).ToArray(),
                "The realizer tries the link's pins in component order, the blocked probe pin first.");
            var added = island.Members.Single(m => m.RequiresStub).Pin;
            Assert.AreEqual(new PinEndpoint(probeId, drawnPin.Number), added.Endpoint);
            Assert.IsTrue(added.CreatedSymbol);

            // The realizer's own §6.4 rule on a fresh live measurement of the redrawn sheet, with TP900 as a candidate and the
            // label measured on each probe pin: the first pin's label runs over the wire's turn one grid in front of it, and
            // the second pin's label is admitted.
            var judged = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = linked.State.Revision.Clone() };
            judged.Candidates.Add(created.Select(c => c.Clone()));
            foreach (var candidate in island.JoinCandidates)
            {
                var pin = placed.Single(p => p.Pin.Id.Value == candidate.PlacedPinId.ToString("D")).Pin;
                var spin = SchematicConnectionGeometry.Spin(SchematicConnectionGeometry.Outward(pin));
                var id = SchematicConnectionIdentity.Probe(revisionModel, screenId, LocalLabel.Descriptor, "LINK", spin, SchematicLabelShape.SlshUnknown, candidate.PlacedPinId, 0);
                judged.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Local, id, pin.Position.Clone(), "LINK", spin, policy)));
            }
            var judgedGeometry = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(judged, token);
            var linkedRoot = linked.Electrical.Hierarchy.Data.Instances.Single(x => x.Metadata.Document.Equals(rootDocument));
            var islandItems = island.AnchorItemIds.Concat(island.Members.Select(m => m.Pin.PlacedPinId)).ToArray();
            var verdicts = island.JoinCandidates.Select((c, i) => (Candidate: c, Refusal: SchematicConnectionRealizer.AnchorLabelRefusal(linkedRoot, judgedGeometry, c.SymbolId,
                judgedGeometry.Obstacles.Single(o => Guid.Parse(o.Id.Value) == c.SymbolId).SymbolPins.Pins.Single(p => Guid.Parse(p.Id.Value) == c.PlacedPinId),
                judgedGeometry.ItemCandidates[i].Bounds, islandItems, policy))).ToArray();
            // Foreign points are taken in position order, then by owner: the turn holds the ends of the first two wires.
            var turn = new[] { Guid.Parse(wires[0].Id.Value), Guid.Parse(wires[1].Id.Value) }.Order().First();
            Assert.AreEqual("wireend point of " + turn.ToString("D") + " lies inside the label", verdicts[0].Refusal,
                "A label on the first probe pin is refused for the wire's turn in front of it.");
            Assert.IsNull(verdicts[1].Refusal, "A label on the second probe pin is admitted.");

            var realization = await RealizeLive("join-turned-link", adopted, revision, plan, advertised, "join-turned-link.recovery.json");
            var anchor = realization.Generated.Single(g => g.Role == GeneratedConnectionRole.AnchorLabel);
            Assert.IsFalse(realization.Generated.Any(g => g.Role == GeneratedConnectionRole.StubWire && island.JoinCandidates.Any(c => c.PlacedPinId == g.PlacedPinId)),
                "No join stub had room: each would run along the redrawn link.");
            Assert.AreEqual(island.JoinCandidates[1].PlacedPinId, anchor.PlacedPinId, "After the first probe pin is refused, the second is named.");
            var label = realization.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true).Select(o => o.Create.Unpack<LocalLabel>())
                .Single(l => l.Id.Value == anchor.Id.ToString("D"));
            Assert.AreEqual(p1, label.Position, "The label sits on the second probe pin itself.");
            Assert.AreEqual(SchematicConnectionGeometry.Spin(outward), label.SpinStyle);
            Assert.AreEqual("LINK", label.Text.Text_);
            CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel },
                realization.Generated.Where(g => g.PlacedPinId == added.PlacedPinId).Select(g => g.Role).ToArray(), "The new probe gets its own stub and label.");
            var stubId = realization.Generated.Single(g => g.PlacedPinId == added.PlacedPinId && g.Role == GeneratedConnectionRole.StubWire).Id.ToString("D");
            var stubLabelId = realization.Generated.Single(g => g.PlacedPinId == added.PlacedPinId && g.Role == GeneratedConnectionRole.StubLabel).Id.ToString("D");
            var stubWire = realization.Operations.Where(o => o.Create?.Is(SchematicLine.Descriptor) == true).Select(o => o.Create.Unpack<SchematicLine>()).Single(w => w.Id.Value == stubId);
            var stubLabel = realization.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true).Select(o => o.Create.Unpack<LocalLabel>()).Single(l => l.Id.Value == stubLabelId);
            Assert.AreEqual(new Vector2 { XNm = chosenProbe.Pin.X, YNm = chosenProbe.Pin.Y }, stubWire.Start, "TP900's stub starts on its pin.");
            Assert.AreEqual(new Vector2 { XNm = chosenProbe.End.X, YNm = chosenProbe.End.Y }, stubWire.End,
                "TP900's stub has the length KiCad's measurement predicts: " + chosenProbe.Multiple + " grids.");
            Assert.AreEqual(stubWire.End, stubLabel.Position);
            Assert.HasCount(3, realization.Generated);
            Assert.AreEqual(joinPart.Units, realization.Operations.Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true), "The new probe is created with it.");
            // The root sheet's library cache gains exactly TP900's declared definition; every definition it held is kept.
            var cache = realization.Operations.Single(o => o.ReplaceLibraryCache is not null).ReplaceLibraryCache;
            CollectionAssert.AreEquivalent(rootScreen.CachedSymbols.Select(c => c.CacheKey).Append("Automation:JoinProbe").ToArray(),
                cache.Definitions.Select(d => d.CacheKey).ToArray(), "The root sheet's cache gains exactly TP900's definition.");
            Assert.IsTrue(realization.Diagnostics.Any(d => d.Code == SchematicConnectionErrors.ExistingNetNamedByRealization && d.NetId == link.Id));

            // One native undo restores the probe link as it was.
            await FocusedSchematicShortcut(client, document, processId, display, "z", token);
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                while (true)
                {
                    var now = (await Capture()).Electrical.Hierarchy.Data;
                    if (SchematicHierarchyDelta.Plan(data, now, token).Count == 0 && SchematicHierarchyDelta.Plan(now, data, token).Count == 0) break;
                    await Task.Delay(250, wait.Token);
                }
            }
            var result = new { depthNm = depth, turnNm = grid, linkMoved = new { shift.Dx, shift.Dy },
                blockedPin = island.JoinCandidates[0].PlacedPinId, namedPin = anchor.PlacedPinId, anchorLabel = anchor.Id,
                candidates = verdicts.Select(v => new { pin = v.Candidate.PlacedPinId, admitted = v.Refusal is null, whyNot = v.Refusal }).ToArray(),
                labelPosition = new { label.Position.XNm, label.Position.YNm }, spin = label.SpinStyle.ToString(),
                probe = new { declaredFrom = tp1Key, orientationsTried = tries, rotation = chosenProbe.Degrees,
                    at = new { chosenProbe.X, chosenProbe.Y }, stubGrids = chosenProbe.Multiple,
                    stub = new { from = new { chosenProbe.Pin.X, chosenProbe.Pin.Y }, to = new { chosenProbe.End.X, chosenProbe.End.Y } } },
                generated = realization.Generated.Select(g => g.Role.ToString()).ToArray(), undone = true };
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-join-anchor-label.json"), JsonSerializer.Serialize(result), token);
            return result;
        }


        // Each moved unit is a native symbol on the root sheet with the component's own reference, the
        // same declared definition as its sibling unit on the channel sheet, and exactly its unit's pins.
        async Task VerifyCrossSheetUnits(SchematicDesign design)
        {
            if (crossSheet.Length == 0) return;
            var native = (await Capture()).Electrical.Hierarchy.Data;
            var symbols = SchematicModelProjection.NativeSymbols(design, native);
            var paths = design.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => b.NativePath.Select(id => id.ToString("D")).ToArray());
            var persisted = design.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
            var nativeIds = design.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
            string[] Placed(SchematicSymbolInstance symbol) => [.. symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
                .Where(p => p.Pin.LibraryPinId is not null && ((p.Child.Unit?.Unit ?? 0) == 0 || p.Child.Unit!.Unit == symbol.Unit.Unit)
                    && ((p.Child.BodyStyle?.Style ?? 0) == 0 || p.Child.BodyStyle!.Style == (symbol.BodyStyle?.Style ?? 1)))
                .Select(p => p.Pin.Number).Order(StringComparer.Ordinal)];
            string[] Declared(int unit) => [.. part.Pins.Where(p => p.Unit == 0 || p.Unit == unit).Select(p => p.Number).Order(StringComparer.Ordinal)];
            foreach (var unit in crossSheet)
            {
                Assert.AreEqual(unit, persisted[unit.Id] with { Placement = null }, "The published XML keeps the unit on its own sheet.");
                var component = design.Engineering.Circuit.Components.Single(c => c.Id == unit.ComponentId);
                Assert.AreNotEqual(rootInstance.Id, component.SheetInstanceId);
                var moved = symbols[unit.Id];
                CollectionAssert.AreEqual(paths[rootInstance.Id], moved.Path.Path.Select(p => p.Value).ToArray());
                Assert.AreEqual(component.Reference, moved.ReferenceField.Text.Text_);
                Assert.AreEqual(unit.Unit, moved.Unit.Unit);
                var record = moved.InstanceRecords.Records.Single();
                CollectionAssert.AreEqual(paths[rootInstance.Id], record.Path.Select(p => p.Value).ToArray());
                Assert.AreEqual((component.Reference, unit.Unit), (record.Reference, record.Unit));
                CollectionAssert.AreEqual(Declared(unit.Unit), Placed(moved));
                foreach (var sibling in design.Engineering.Circuit.Symbols.Where(s => s.ComponentId == unit.ComponentId && s.Id != unit.Id))
                {
                    var stays = symbols[sibling.Id];
                    Assert.IsNull(sibling.SheetInstanceId);
                    CollectionAssert.AreEqual(paths[component.SheetInstanceId], stays.Path.Path.Select(p => p.Value).ToArray());
                    Assert.AreEqual(component.Reference, stays.ReferenceField.Text.Text_);
                    Assert.AreEqual(moved.LibraryId, stays.LibraryId); Assert.AreEqual(moved.LibName, stays.LibName);
                    Assert.AreEqual(moved.Definition.Id, stays.Definition.Id);
                    CollectionAssert.AreEqual(Declared(sibling.Unit), Placed(stays));
                }
            }
            Assert.AreEqual(crossSheet.Length, crossSheet.Select(u => nativeIds[u.Id]).Distinct().Count(),
                "Units of different components on one single-instance sheet stay separate symbols.");
            var channelUnits = design.Engineering.Circuit.Symbols.Where(s => crossSheet.Any(u => u.ComponentId == s.ComponentId) && s.SheetInstanceId is null)
                .GroupBy(s => s.Unit).ToArray();
            Assert.IsTrue(channelUnits.All(g => g.Count() == crossSheet.Length && g.Select(s => nativeIds[s.Id]).Distinct().Count() == 1),
                "Repeated channel instances still share one physical symbol for each unit that stays there.");
            // Every unit but the moved last one stays, including a later unit (unit 2), not just unit 1.
            CollectionAssert.AreEqual(Enumerable.Range(1, part.Units - 1).ToArray(), channelUnits.Select(g => g.Key).Order().ToArray());
            Assert.IsTrue(channelUnits.Any(g => g.Key > 1), "A later unit must stay on the repeated channel sheets.");
            foreach (var shared in channelUnits)
            {
                var occurrencesOfUnit = shared.ToArray();
                var symbol = symbols[occurrencesOfUnit[0].Id];
                // One native symbol per unit, recorded once for each channel instance with that channel's reference.
                var owners = occurrencesOfUnit.Select(s => design.Engineering.Circuit.Components.Single(c => c.Id == s.ComponentId)).ToArray();
                CollectionAssert.AreEquivalent(owners.Select(c => (string.Join('/', paths[c.SheetInstanceId]), c.Reference, shared.Key)).ToArray(),
                    symbol.InstanceRecords.Records.Select(r => (string.Join('/', r.Path.Select(p => p.Value)), r.Reference, r.Unit)).ToArray(),
                    $"Unit {shared.Key} is one symbol shared by every channel instance.");
                CollectionAssert.AreEqual(Declared(shared.Key), Placed(symbol), $"The shared unit {shared.Key} carries exactly its declared pins.");
            }
        }

        // An inconsistent declaration: the root-owned probe places its last unit on only one of the
        // two repeated channel instances, so their shared screen would show that unit on the other
        // channel with no model occurrence for it. Both the layout proposal and a real public apply
        // must refuse it with the exact code, leaving KiCad, the recovery record and XML untouched.
        async Task<StoredDesignRecovery> RejectPartialRepeatedSheetUnit(StdioMcpFixture host,
            List<SchematicLayoutRegion> regions, string instructions, SchematicDesign proposed)
        {
            const string Code = "created_unit_sheet_coverage_mismatch";
            var rootComponent = components.Single(c => createdIds.Contains(c.Id) && c.SheetInstanceId == rootInstance.Id);
            var channel = baseline.Engineering.Circuit.SheetInstances.Where(s => s.ParentId == rootInstance.Id).OrderBy(s => s.Id).First();
            SchematicDesign Misplaced(SchematicDesign design) => design with { Engineering = design.Engineering with
            { Circuit = design.Engineering.Circuit with { Symbols = design.Engineering.Circuit.Symbols.Select(s =>
                s.ComponentId == rootComponent.Id && s.Unit == part.Units ? s with { SheetInstanceId = channel.Id } : s).ToArray() } } };
            byte[] original = await File.ReadAllBytesAsync(path, token);
            var nativeBefore = await Capture();
            var current = store.Read()!;
            string baselineXml = SchematicDesignXml.Write(current.State.Baseline, []);
            var results = new Dictionary<string, JsonElement>();

            byte[] unplaced = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(Misplaced(desired), []));
            await File.WriteAllBytesAsync(path, unplaced, token);
            current = store.Save(current.State with { DesiredFileBytes = unplaced }, current.RevisionToken);
            var layout = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = current.RevisionToken, gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L,
                regions, userInstructions = instructions });
            results["layout"] = layout;
            Assert.IsTrue(layout.GetProperty("isError").GetBoolean(), layout.GetRawText());
            string? layoutCode = JsonDocument.Parse(layout.GetProperty("content")[0].GetProperty("text").GetString()!)
                .RootElement.GetProperty("code").GetString();
            Assert.AreEqual(Code, layoutCode, layout.GetRawText());

            byte[] placed = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(Misplaced(proposed), []));
            await File.WriteAllBytesAsync(path, placed, token);
            current = store.Save(current.State with { DesiredFileBytes = placed }, current.RevisionToken);
            var apply = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath,
                designPath = path, expectedRevisionToken = current.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            results["apply"] = apply;
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-cross-sheet-rejection.json"),
                JsonSerializer.Serialize(results), token);
            Assert.IsTrue(apply.GetProperty("isError").GetBoolean(), apply.GetRawText());
            string? applyCode = apply.GetProperty("structuredContent").GetProperty("errorCode").GetString();
            Assert.AreEqual(Code, applyCode, apply.GetRawText());

            Assert.AreEqual(nativeBefore, await Capture(), "A rejected declaration must not reach the native editor.");
            var after = store.Read()!;
            Assert.AreEqual(current.RevisionToken, after.RevisionToken, "A rejected declaration must not advance recovery.");
            Assert.AreEqual(baselineXml, SchematicDesignXml.Write(after.State.Baseline, []));
            Assert.IsFalse(after.State.HasPendingWork);
            CollectionAssert.AreEqual(placed, await File.ReadAllBytesAsync(path, token), "A rejected apply must not publish XML.");
            crossSheetRejection = new { layoutErrorCode = layoutCode, applyErrorCode = applyCode };
            await File.WriteAllBytesAsync(path, original, token);
            return store.Save(after.State with { DesiredFileBytes = original }, after.RevisionToken);
        }
        // CN-1 gate (cn1-wiring-intent.md §4.1, §8.3) against this editor's real handshake and captured state.
        // A saved XML revision that only connects drawn pins of the created components is exactly what a
        // connection-realizing editor would receive, yet this editor does not advertise
        // schematic.connection-realization.v1, so nothing admits it: the public plan keeps its existing
        // result and KiCad, the recovery record and the XML file stay untouched.
        async Task<object> RequireConnectionRealizationGated(StdioMcpFixture host)
        {
            var session = await client.HandshakeAsync(token);
            Assert.AreEqual(instanceId, session.InstanceId);
            Assert.IsFalse(session.Capabilities.Contains(SchematicConnectedAddition.NativeCapability),
                "This editor must not advertise connection realization before every native piece exists: " + string.Join(",", session.Capabilities));
            var current = store.Read()!;
            Assert.IsFalse(current.State.HasPendingWork);
            var circuit = current.State.Baseline.Engineering.Circuit;
            var ends = circuit.Components.Where(c => createdIds.Contains(c.Id)).OrderBy(c => c.Id).Take(2).ToArray();
            var drawnPin = part.Pins.Where(p => p.Unit is 0 or 1).OrderBy(p => p.Number, StringComparer.Ordinal).First();
            Assert.HasCount(2, ends);
            Assert.IsFalse(circuit.Nets.Any(n => n.Pins.Any(p => ends.Any(c => c.Id == p.ComponentId))), "Created components start unconnected.");
            Guid probeNet = Guid.NewGuid();
            var connected = current.State.Baseline with { Engineering = current.State.Baseline.Engineering with { Circuit = circuit with
                { Nets = [.. circuit.Nets, new(probeNet, "XML_CONNECTION_PROBE", ends.Select(c => new PinEndpoint(c.Id, drawnPin.Number)).ToArray())] } } };
            byte[] connectedBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(connected, []));
            var probe = current.State with { DesiredFileBytes = connectedBytes };
            var probeDesign = DesignRecoveryStore.ReadDesired(probe);
            var real = SchematicConnectedAddition.Classify(probe, probeDesign, session, token);
            Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, real.Kind, real.ErrorCode + ": " + real.ErrorMessage);
            var advertised = session.Clone(); advertised.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
            var withCapability = SchematicConnectedAddition.Classify(probe, probeDesign, advertised, token);
            Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, withCapability.Kind,
                "The real captured state must be recognized as a connected addition: " + withCapability.ErrorCode + " " + withCapability.ErrorMessage);
            CollectionAssert.AreEqual(new[] { probeNet }, withCapability.ChangedNetIds.ToArray());
            Assert.IsEmpty(withCapability.AddedComponentIds);

            var nativeBefore = await Capture();
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            var planned = store.Save(probe, current.RevisionToken);
            // The planner's own result for the saved record is what the public tool must report unchanged.
            var expected = SchematicSynchronizationPlanner.Plan(store.Read()!.State, token);
            Assert.IsTrue(expected.CanPrepare, expected.ErrorCode + ": " + expected.ErrorMessage);
            Assert.IsNull(expected.Connections);
            var plan = await host.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = planned.RevisionToken });
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connection-gate-plan.json"), plan.GetRawText(), token);
            var planContent = plan.GetProperty("structuredContent");
            string? planCode = planContent.GetProperty("errorCode").GetString();
            // Today's general path: a preparable candidate with no native operations whose new connection
            // is left to post-apply native connectivity validation, exactly as the planner computes it.
            RequireToolSuccess(plan);
            Assert.IsTrue(planContent.GetProperty("canPrepare").GetBoolean(), plan.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, planContent.GetProperty("errorCode").ValueKind, plan.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, planContent.GetProperty("errorMessage").ValueKind, plan.GetRawText());
            Assert.AreEqual(0, planContent.GetProperty("nativeOperationsJson").GetArrayLength(), plan.GetRawText());
            Assert.IsTrue(planContent.GetProperty("nativeConnectivityValidationRequired").GetBoolean(), plan.GetRawText());
            Assert.IsFalse(planContent.GetProperty("observedConnectivity").GetProperty("ConnectivityEquivalent").GetBoolean(),
                "The unrealized connection must still show as a native difference.");
            Assert.AreEqual(expected.CanPrepare, planContent.GetProperty("canPrepare").GetBoolean());
            Assert.AreEqual(expected.ErrorCode, planCode);
            Assert.AreEqual(expected.CandidateXml, planContent.GetProperty("candidateDesignXml").GetString(),
                "The public plan must publish exactly the planner's general-path candidate.");
            CollectionAssert.AreEqual(expected.NativeOperations.Select(o => SchematicJson.Formatter.Format(o)).ToArray(),
                planContent.GetProperty("nativeOperationsJson").EnumerateArray().Select(o => o.GetString()).ToArray());
            Assert.AreEqual(expected.NativeConnectivityValidationRequired, planContent.GetProperty("nativeConnectivityValidationRequired").GetBoolean());
            Assert.AreEqual(expected.ObservedConnectivity!.ConnectivityEquivalent,
                planContent.GetProperty("observedConnectivity").GetProperty("ConnectivityEquivalent").GetBoolean());
            Assert.AreEqual(nativeBefore, await Capture(), "Planning a gated connected addition must not change the native document.");
            Assert.AreEqual(planned.RevisionToken, store.Read()!.RevisionToken, "Planning must not advance recovery.");
            CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Planning must not publish XML.");
            store.Save(current.State, planned.RevisionToken);
            var intents = await RequireRealConnectionIntents(store.Read()!.State, session, advertised);
            return new { nativeCapabilities = session.Capabilities.ToArray(), classification = real.Kind.ToString(),
                classificationIfAdvertised = withCapability.Kind.ToString(), changedNets = withCapability.ChangedNetIds,
                publicPlanCanPrepare = planContent.GetProperty("canPrepare").GetBoolean(), publicPlanErrorCode = planCode,
                publicPlanNativeOperations = planContent.GetProperty("nativeOperationsJson").GetArrayLength(),
                publicPlanNativeConnectivityValidationRequired = planContent.GetProperty("nativeConnectivityValidationRequired").GetBoolean(),
                publicPlanMatchesPlanner = true, intents };
        }

        // CN-1 connection intent (cn1-wiring-intent.md §5) planned from this editor's real captured state, as an
        // editor that advertises schematic.connection-realization.v1 would receive it. Planning is offline and this
        // editor cannot draw the connections yet, so each plan is only inspected: nothing is saved, published or sent.
        // The repeated channel sheet shares one symbol per unit between its two instances, and the root's two probe
        // pins are joined by a wire and the local label SIGNAL.
        async Task<object> RequireRealConnectionIntents(DesignRecoveryState state, AutomationSession session, AutomationSession advertised)
        {
            var nativeBefore = await Capture();
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            string revisionBefore = store.Read()!.RevisionToken;
            var circuit = state.Baseline.Engineering.Circuit;
            var rootSheet = circuit.SheetInstances.Single(s => s.ParentId is null);
            var drawnPin = part.Pins.Where(p => p.Unit is 0 or 1).OrderBy(p => p.Number, StringComparer.Ordinal).First();
            var mine = circuit.Components.Where(c => createdIds.Contains(c.Id)).ToArray();
            var channels = mine.Where(c => c.SheetInstanceId != rootSheet.Id).OrderBy(c => c.Id).ToArray();
            var rootProbe = mine.Single(c => c.SheetInstanceId == rootSheet.Id);
            Assert.HasCount(2, channels);
            (SchematicSynchronizationPlan Real, SchematicSynchronizationPlan Realizing, DesignRecoveryState Revision) PlanNets(IEnumerable<CircuitNet> nets)
            {
                var design = state.Baseline with { Engineering = state.Baseline.Engineering with { Circuit = circuit with { Nets = [.. nets] } } };
                var revision = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [])) };
                return (SchematicSynchronizationPlanner.Plan(revision, session, token), SchematicSynchronizationPlanner.Plan(revision, advertised, token), revision);
            }

            // The two channel probes share one physical pin, so joining them draws one hierarchical label on the
            // shared channel sheet and one sheet pin per channel on the root.
            var pairNet = new CircuitNet(Guid.NewGuid(), "XML_CHANNEL_PAIR", [.. channels.Select(c => new PinEndpoint(c.Id, drawnPin.Number))]);
            var pair = PlanNets([.. circuit.Nets, pairNet]);
            Assert.IsNull(pair.Real.Connections, "This editor's own handshake admits no wiring.");
            var pairIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(pair.Realizing);
            Assert.AreEqual(ConnectionScope.Local, pairIntent.Nets.Single().Scope);
            var channelScreen = pairIntent.Screens.Single(s => s.InstancePathKeys.Count == 2);
            var shared = channelScreen.Islands.Single();
            Assert.AreEqual("XML_CHANNEL_PAIR", shared.LabelText);
            Assert.IsNotNull(shared.UplinkSheetSymbolId);
            Assert.AreEqual(ConnectionMemberRole.Signal, shared.Members.Single().Role, "The probe pin is a visible passive pin.");
            Assert.IsTrue(shared.Members.Single().RequiresStub);
            var rootIsland = pairIntent.Screens.Single(s => s.InstancePathKeys.Count == 1).Islands.Single();
            Assert.IsEmpty(rootIsland.Members); Assert.HasCount(2, rootIsland.ChildSheetSymbolIds);
            Assert.HasCount(2, pairIntent.Ports);
            Assert.IsTrue(pairIntent.Ports.All(p => !p.SheetPinExists && !p.UplinkLabelExists));
            SchematicConnectionIntentBuilderTests.RequireGroups(pairIntent,
                [SchematicConnectionIntentBuilderTests.Keys(state.Baseline, [.. channels.Select(c => (c.Id, drawnPin.Number))])]);
            Assert.IsEmpty(pairIntent.CreatedSymbolIds);
            // CN-1 §6: the label-stub realizer measures this editor and draws the pair: one shared hierarchical label on
            // the repeated channel sheet, and on the root one new sheet pin per channel with its own stub and label.
            var pairRealization = await RealizeLive("channel-pair", state, pair.Revision, pair.Realizing, advertised);
            var channelItems = pairRealization.Generated.Where(g => g.ScreenId == channelScreen.ScreenId).ToArray();
            CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel },
                channelItems.Select(g => g.Role).ToArray(), "The shared channel pin gets one stub and one label for both channels.");
            Assert.AreEqual(Any.Pack(new HierarchicalLabel()).TypeUrl, channelItems.Single(g => g.Role == GeneratedConnectionRole.StubLabel).TypeUrl);
            var rootItems = pairRealization.Generated.Where(g => g.ScreenId != channelScreen.ScreenId).ToArray();
            Assert.HasCount(2, rootItems.Where(g => g.Role == GeneratedConnectionRole.SheetPin).ToArray());
            Assert.HasCount(2, rootItems.Where(g => g.Role == GeneratedConnectionRole.SheetPinWire).ToArray());
            Assert.HasCount(2, rootItems.Where(g => g.Role == GeneratedConnectionRole.SheetPinLabel).ToArray());
            CollectionAssert.AreEquivalent(rootIsland.ChildSheetSymbolIds.ToArray(), rootItems.Where(g => g.Role == GeneratedConnectionRole.SheetPin)
                .Select(g => g.SheetSymbolId!.Value).ToArray());
            var sheetUpdates = pairRealization.Operations.Where(o => o.Update is not null && o.Update.Is(SheetSymbol.Descriptor))
                .Select(o => o.Update.Unpack<SheetSymbol>()).ToArray();
            Assert.HasCount(2, sheetUpdates);
            Assert.IsTrue(sheetUpdates.All(u => u.Pins.Count(p => p.Text.Text_ == "XML_CHANNEL_PAIR") == 1));

            // The root probe joins the existing probe link: the link's own label names it and only the new pin is drawn.
            var link = circuit.Nets.Single();
            var joined = PlanNets([link with { Pins = [.. link.Pins, new PinEndpoint(rootProbe.Id, drawnPin.Number)] }]);
            Assert.IsNull(joined.Real.Connections);
            var joinIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(joined.Realizing);
            var joinIsland = joinIntent.Screens.Single().Islands.Single();
            Assert.AreEqual("SIGNAL", joinIsland.LabelText, "The existing local label names the connection.");
            Assert.IsTrue(joinIsland.AnchorHasMatchingDriver); Assert.IsFalse(joinIsland.JoinRequired);
            Assert.AreEqual(new PinEndpoint(rootProbe.Id, drawnPin.Number), joinIsland.Members.Single(m => m.RequiresStub).Pin.Endpoint);
            Assert.IsTrue(joinIsland.Members.Where(m => m.Pin.Endpoint.ComponentId != rootProbe.Id).All(m => m.AlreadyConnected));
            var rootScreen = state.Observed.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1);
            var signal = rootScreen.Items.Where(i => i.Is(LocalLabel.Descriptor)).Select(i => i.Unpack<LocalLabel>()).Single(l => l.Text.Text_ == "SIGNAL");
            CollectionAssert.Contains(joinIsland.AnchorItemIds.ToArray(), Guid.Parse(signal.Id.Value), "The anchor is the editor's own connection.");
            SchematicConnectionIntentBuilderTests.RequireGroups(joinIntent, [SchematicConnectionIntentBuilderTests.Keys(state.Baseline,
                [.. link.Pins.Select(p => (p.ComponentId, p.Pin)), (rootProbe.Id, drawnPin.Number)])]);
            // The new root probe pin gets one stub and a local label reusing the link's own name.
            var joinRealization = await RealizeLive("join-existing-link", state, joined.Revision, joined.Realizing, advertised);
            CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel },
                joinRealization.Generated.Select(g => g.Role).ToArray());
            var joinLabel = joinRealization.Operations.Where(o => o.Create is not null && o.Create.Is(LocalLabel.Descriptor))
                .Select(o => o.Create.Unpack<LocalLabel>()).Single();
            Assert.AreEqual("SIGNAL", joinLabel.Text.Text_);

            // The root probe with one channel probe cannot be drawn on the shared channel sheet for one instance only.
            var mixed = PlanNets([.. circuit.Nets, new CircuitNet(Guid.NewGuid(), "XML_MIXED",
                [new(rootProbe.Id, drawnPin.Number), new(channels[0].Id, drawnPin.Number)])]);
            Assert.AreEqual(SchematicConnectionErrors.ConnectedRepeatedScreenDivergent, mixed.Realizing.ErrorCode, mixed.Realizing.ErrorMessage);
            Assert.IsNull(mixed.Realizing.Candidate); Assert.IsNull(mixed.Realizing.Connections);

            Assert.AreEqual(nativeBefore, await Capture(), "Planning connections must not change the native document.");
            Assert.AreEqual(revisionBefore, store.Read()!.RevisionToken, "Planning connections must not advance recovery.");
            CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Planning connections must not publish XML.");
            var result = new
            {
                channelPair = SchematicConnectionIntentBuilder.Summary(pairIntent),
                channelPairRealization = pairRealization.Generated.Select(g => new { id = g.Id, role = g.Role.ToString(), screen = g.ScreenId }).ToArray(),
                joinExistingLink = SchematicConnectionIntentBuilder.Summary(joinIntent),
                joinExistingLinkRealization = joinRealization.Generated.Select(g => new { id = g.Id, role = g.Role.ToString(), screen = g.ScreenId }).ToArray(),
                joinAnchorItems = joinIsland.AnchorItemIds.Count,
                rootAndOneChannel = new { errorCode = mixed.Realizing.ErrorCode, errorMessage = mixed.Realizing.ErrorMessage }
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connection-intents.json"), JsonSerializer.Serialize(result), token);
            return new { channelPairIslands = channelScreen.Islands.Count, channelPairPorts = pairIntent.Ports.Count, joinLabel = joinIsland.LabelText,
                joinAnchorItems = joinIsland.AnchorItemIds.Count, rootAndOneChannel = mixed.Realizing.ErrorCode,
                channelPairGenerated = pairRealization.Generated.Count, joinExistingLinkGenerated = joinRealization.Generated.Count };
        }

        // Realize a planned intent against this editor's own measurements without applying anything (the native
        // connectivity assertion that would admit the batch is lane 2C's). The lane entry point must turn the same
        // recorded measurements into the identical batch, and the recording is kept as a replay fixture beside the saved
        // record the revision was planned from (<instance>-realization-<record>.gz, compressed because the evidence artifact
        // has a fixed size cap; committed decompressed as <record>).
        async Task<SchematicConnectionRealization> RealizeLive(string name, DesignRecoveryState saved, DesignRecoveryState revision,
            SchematicSynchronizationPlan plan, AutomationSession advertised, string record = "editor.recovery.json")
        {
            var checkpoint = await Capture();
            Assert.AreEqual(revision.NativeRevision.Epoch, checkpoint.State.Revision.Epoch);
            Assert.AreEqual(revision.NativeRevision.Sequence, checkpoint.State.Revision.Sequence);
            Assert.AreEqual(revision.Observed, checkpoint.Electrical.Hierarchy.Data, "The executor realizes only the observed checkpoint.");
            var recorded = new List<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)>();
            async Task<SchematicPlacementGeometry> Live(MeasureSchematicPlacement request, CancellationToken cancellation)
            {
                var reply = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, cancellation);
                recorded.Add((request.Clone(), reply.Clone()));
                return reply;
            }
            var policy = SchematicConnectionPolicy.FromSnapshot(checkpoint.Electrical.Hierarchy.Data);
            string kept = Path.Combine(evidence, instanceId + "-realization-" + record);
            SchematicConnectionRealization realization;
            try { realization = await SchematicConnectionRealizer.RealizeAsync(plan.Connections!, plan.Candidate!, checkpoint, Live, policy, token); }
            catch (AutomationException error)
            {
                // A refused realization keeps what the editor answered beside the saved record, so it can be replayed.
                await Keep();
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-realization-" + name + ".refused.json"),
                    SchematicConnectionRealizerTests.FormatRecording(name, revision, checkpoint, recorded, null, record, error), CancellationToken.None);
                throw;
            }
            Assert.IsNotNull(realization.Operations[^1].AssertConnectivity);
            Assert.IsTrue(recorded.Any(r => r.Request.ItemCandidates.Count != 0), "Label prototypes are measured natively.");
            foreach (var screen in plan.Connections!.Screens)
            foreach (var instance in screen.InstancePathKeys)
                Assert.IsTrue(recorded.Any(r => string.Join('/', r.Request.Document.SheetPath.Path.Select(p => p.Value)) == instance),
                    "Every instance path of every realized screen is measured: " + instance);
            // Every generated connection point lies inside the measured page inset.
            foreach (var operation in realization.Operations.Where(o => o.Create is not null))
            {
                var created = SchematicItemDelta.Index([operation.Create]).Single().Value;
                var point = created switch { SchematicLine line => line.End, LocalLabel label => label.Position, GlobalLabel label => label.Position,
                    HierarchicalLabel label => label.Position, _ => null };
                if (point is null) continue;
                var page = recorded.First(r => r.Request.Document.Equals(operation.TargetDocument)).Reply.PageBounds;
                Assert.IsTrue(point.XNm >= page.Position.XNm + policy.PageInsetNm && point.XNm <= page.Position.XNm + page.Size.XNm - policy.PageInsetNm
                    && point.YNm >= page.Position.YNm + policy.PageInsetNm && point.YNm <= page.Position.YNm + page.Size.YNm - policy.PageInsetNm, name);
            }
            var prepared = await SchematicConnectedAddition.RealizeAsync(SchematicConnectionRealizerTests.Replay(recorded), advertised, revision, plan, checkpoint, token);
            CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), prepared.Operations.Select(o => o.ToByteString()).ToArray(),
                "The same measurements give the same batch (I9).");
            Assert.AreEqual(SchematicDesignXml.Write(realization.Design, []), Encoding.UTF8.GetString(prepared.PlannedDesignFileBytes));
            // Replay fixture (automation/tests/fixtures/connection-realization): this scenario's nets and measurements.
            await Keep();
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-realization-" + name + ".measurement.json"),
                SchematicConnectionRealizerTests.FormatRecording(name, revision, checkpoint, recorded, realization, record), token);
            return realization;

            // The saved record, once per record name, compressed.
            async Task Keep()
            {
                if (File.Exists(kept + ".gz")) return;
                new DesignRecoveryStore(kept).Save(saved with { LastSynchronization = null }, null);
                await using (var input = File.OpenRead(kept))
                await using (var output = File.Create(kept + ".gz"))
                await using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
                    await input.CopyToAsync(zip, CancellationToken.None);
                File.Delete(kept);
                File.Delete(kept + ".lock");
            }
        }

        async Task RequireAgreement(int count, bool afterReload = false)
        {
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(path, token), []);
            if (declaration is not null)
            {
                var persisted = xml.PartSymbols!.Single();
                Assert.AreEqual(declaration.Symbol, persisted.Symbol);
                Assert.AreEqual(declaration.LibraryId, persisted.LibraryId);
            }
            var native = await Capture();
            Assert.AreEqual(count, xml.Engineering.Circuit.Components.Count(c => createdIds.Contains(c.Id)));
            if (afterReload)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-creation-reloaded.xml"),
                    SchematicDataXml.Write(native.Electrical.Hierarchy.Data), token);
                // Loading the newly written file changes source-format provenance,
                // not any persisted schematic object, property or connection.
                var expected = xml.Schematic.Clone();
                foreach (var screen in expected.Instances)
                {
                    var actual = native.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document));
                    Assert.AreEqual(screen.Metadata.WriterNativeFormatVersion, actual.Metadata.LoadedNativeFormatVersion);
                    screen.Metadata.LoadedNativeFormatVersion = actual.Metadata.LoadedNativeFormatVersion;
                }
                Assert.IsEmpty(SchematicHierarchyDelta.Plan(native.Electrical.Hierarchy.Data, expected, token));
            }
            else Assert.IsEmpty(SchematicHierarchyDelta.Plan(native.Electrical.Hierarchy.Data, xml.Schematic, token));
            var comparison = SchematicElectricalComparison.Compare(xml, native.Electrical, [], token);
            Assert.IsTrue(comparison.PinBindingsComplete); Assert.IsTrue(comparison.ConnectivityEquivalent);
            Assert.IsFalse(native.State.NativeContentDirty);
        }
    }
}
