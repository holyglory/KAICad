using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9), lane 2C, ledger p74ee7c1da24272d9: native
    // edits reach the owning block by exact identity. From the S1 seed the Components stage is created through the public
    // layout, plan and apply tools of the production MCP server over STDIO. Then, as a person works in KiCad and in the XML:
    //  The XML also declares a second part, R_sense, drawn with the same library resistor as the fixture's R.
    //  1. The automatic worker starts with the fixture's block graph. Every component is owned, so nothing is written.
    //  2. Connectors placed in KiCad on three sheets become XML components whose identities derive from the circuit, the
    //     sheet path and the symbol's own UUID, of the fixture's connector part (the only part drawn or declared with that
    //     library symbol and exactly its pins). Sheets are not blocks, so a sheet names a block only when one block owns
    //     everything else it shows: J4 on CPU_POWER (only the processor's power unit) is bound to Processor by the worker, as
    //     a new Processor revision in a new root snapshot. J2 on the PSU sheet (parts of PSU and its four children) and J3 on
    //     the root sheet (no owned part) pause the worker with requests; the person binds them to PSU and System with
    //     kicad_diagram_components_set and resumes.
    //  3. An instruction the XML adds to J4 is kept as a detached instruction when undo in KiCad removes it, and is attached
    //     again when redo restores it with the same identities; its Processor binding stays throughout.
    //  4. A resistor placed in KiCad could be the fixture's R or R_sense: the worker pauses with the resolution request and
    //     publishes nothing. The person stops the worker and undoes the placement.
    //  5. With the worker stopped, the XML removes the processor's power unit and the memory; the public plan previews
    //     exactly their two symbol removals, and apply removes them in KiCad. Their bindings stay as detached.
    //  6. Simultaneous conflicting edits of one component: U3 moved in KiCad while the XML removes U3. The worker pauses and
    //     keeps both versions; after undo in KiCad and resume, the XML removal is applied and U3's binding stays detached.
    //  7. Every repeat is a no-op: plan, apply and block ownership change nothing.
    private static async Task VerifyPsuCpuOwnershipSync(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Components);
        var document = context.Root;
        string path = context.DesignPath, blocksPath = context.BlocksPath;
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-ownership-sync-" + name);
        var store = new DesignRecoveryStore(Evidence("recovery.json"));
        Assert.AreEqual(PsuCpuSeed.Sheets, context.Seed);
        Guid designId = PsuCpuIds.Id(0x01, 2), circuitId = PsuCpuIds.Id(0x02, 1), documentId = PsuCpuIds.Id(0x10, 1);
        Guid Block(int n) => PsuCpuIds.Id(0x11, n);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<object>();
        void Step(string name, object? detail = null)
        {
            steps.Add(new { name, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), detail });
            Console.WriteLine($"PSU/CPU ownership sync {instanceId}: {name} at {clock.Elapsed.TotalSeconds:F1}s");
        }
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        static string Sha(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        async Task<string> BlocksSha() => Sha(await File.ReadAllBytesAsync(blocksPath, token));
        async Task<RecursiveBlockGraph> Blocks() => RecursiveBlockGraphXml.Read(await File.ReadAllTextAsync(blocksPath, token));
        async Task<SchematicDesign> Published() => SchematicDesignXml.Read(await File.ReadAllTextAsync(path, token), []);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(), Evidence("host"), Evidence("host.log"), token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };

        // ---- Setup: the Components stage, created from XML through the public tools -------------------------------
        var saved = await PsuCpuFixture.InitializeRecoveryAsync(client, context, store.StatePath, token);
        var seeded = await Capture();
        var (regions, usable, sheetPaths) = await PsuCpuRegions(client, context.Baseline!, expected, seeded.State.Revision, token);
        var components = PsuCpuFixture.Desired(context, PsuCpuStage.Components);
        var fixtureR = components.Engineering.Circuit.Parts.Single(p => p.Id == PsuCpuIds.Id(0x03, 3));
        var sense = fixtureR with { Id = Guid.NewGuid(), Name = "R_sense" };
        saved = await Desire(saved, components with
        {
            Engineering = components.Engineering with { Circuit = components.Engineering.Circuit with { Parts = [.. components.Engineering.Circuit.Parts, sense] } },
            PartSymbols = [.. components.PartSymbols!, context.PartSymbols.Single(p => p.PartId == fixtureR.Id) with { PartId = sense.Id }]
        });
        var layout = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = saved.RevisionToken, gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L, regions,
            userInstructions = "Place each fixture component on its own sheet and keep the processor's power unit on CPU_POWER." });
        await File.WriteAllTextAsync(Evidence("layout.json"), RetainedToolEvidence(layout), token);
        RequireToolSuccess(layout);
        Assert.IsTrue(layout.GetProperty("structuredContent").GetProperty("canPropose").GetBoolean(), layout.GetRawText());
        saved = await Desire(store.Read()!, SchematicDesignXml.Read(layout.GetProperty("structuredContent").GetProperty("desiredXml").GetString()!, []));
        var creation = await Plan("creation-plan");
        Assert.AreEqual(expected.Symbols.Count, Operations(creation).Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true));
        await Apply("creation", mutation: true);
        // KiCad shows exactly the Components stage; the XML's unused R_sense part draws nothing.
        var created = store.Read()!.State.Baseline;
        Assert.IsTrue(created.Engineering.Circuit.Parts.Any(p => p.Id == sense.Id));
        PsuCpuFixture.AssertNative(created with { Engineering = created.Engineering with { Circuit = created.Engineering.Circuit with
            { Parts = [.. created.Engineering.Circuit.Parts.Where(p => p.Id != sense.Id)] } },
            PartSymbols = [.. created.PartSymbols!.Where(p => p.PartId != sense.Id)] }, (await Capture()).Electrical, PsuCpuStage.Components);
        string blocksAtStart = await BlocksSha();
        Step("components created");

        // ---- 1. The worker keeps block ownership; every component is owned ----------------------------------------
        var started = await host.Tool("kicad_design_automatic_sync_start", new { instanceId, recoveryPath = store.StatePath, designPath = path,
            expectedRecoveryRevision = store.Read()!.RevisionToken, blockGraphPath = blocksPath, designId = designId.ToString("D") });
        await File.WriteAllTextAsync(Evidence("worker-start.json"), started.GetRawText(), token);
        RequireToolSuccess(started);
        Assert.IsTrue(started.GetProperty("structuredContent").GetProperty("blockOwnership").GetBoolean());
        string sessionId = started.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
        ulong sequence = started.GetProperty("structuredContent").GetProperty("status").GetProperty("sequence").GetUInt64();
        var statuses = new List<object>();
        await Worker("worker start", s => Phase(s) == "Watching");
        Assert.AreEqual(blocksAtStart, await BlocksSha(), "Every component already has its block: nothing is written.");
        var ownedAtStart = await BlockPlan("block-plan-start");
        Assert.AreEqual(0, ownedAtStart.GetProperty("assignments").GetArrayLength());
        Assert.AreEqual(0, ownedAtStart.GetProperty("resolutionRequests").GetArrayLength());
        Step("worker watching");

        // ---- 2. Connectors placed in KiCad on three sheets -----------------------------------------------------------
        // J2 on the PSU sheet, J3 on the root sheet and J4 on CPU_POWER, one native commit each, while the worker watches.
        var psu = Sheet(PsuCpuIds.Id(0x05, 2));
        var rootSheet = Sheet(PsuCpuIds.Id(0x05, 1));
        var cpuPower = Sheet(PsuCpuIds.Id(0x05, 4));
        var shownAtStart = (await Capture()).Electrical.Hierarchy.Data;
        var j1 = NativeOf(store.Read()!.State.Baseline, shownAtStart, PsuCpuIds.Id(0x09, 1));
        var r1 = NativeOf(store.Read()!.State.Baseline, shownAtStart, PsuCpuIds.Id(0x09, 3));
        var free = usable["PSU"];
        var power = usable["CPU_POWER"];
        static long Grid(long nm) => (nm + 1_270_000 - 1) / 1_270_000 * 1_270_000;
        var j2 = Place(j1, "J2", psu, Grid(free.LeftNm + 25_400_000), Grid(free.BottomNm + 20_320_000));
        var j3 = Place(j1, "J3", rootSheet, 25_400_000, 152_400_000);
        var j4 = Place(j1, "J4", cpuPower, Grid(power.LeftNm + 25_400_000), Grid(power.BottomNm + 20_320_000));
        IReadOnlyList<Guid> NativePathOf(Guid sheet) => store.Read()!.State.Baseline.SheetBindings.Single(b => b.SheetInstanceId == sheet).NativePath;
        Guid Adopted(string kind, Guid sheet, SchematicSymbolInstance symbol) =>
            SchematicNativeAdditionProjection.AdoptedIdentity(kind, circuitId, NativePathOf(sheet), Guid.Parse(symbol.Id.Value));
        Guid j2Component = Adopted("component", PsuCpuIds.Id(0x05, 2), j2), j2Occurrence = Adopted("occurrence", PsuCpuIds.Id(0x05, 2), j2);
        Guid j3Component = Adopted("component", PsuCpuIds.Id(0x05, 1), j3);
        Guid j4Component = Adopted("component", PsuCpuIds.Id(0x05, 4), j4), j4Occurrence = Adopted("occurrence", PsuCpuIds.Id(0x05, 4), j4);
        Guid j2Native = Guid.Parse(j2.Id.Value), j4Native = Guid.Parse(j4.Id.Value);
        await NativeBatch("Place a test connector on the PSU sheet", psu, new SchematicItemOperation { Create = Any.Pack(j2) });
        await NativeBatch("Place a connector on the root sheet", rootSheet, new SchematicItemOperation { Create = Any.Pack(j3) });
        await NativeBatch("Place a connector beside the processor's power unit", cpuPower, new SchematicItemOperation { Create = Any.Pack(j4) });

        // The worker adopts them and binds what exact identities decide. J4: everything else CPU_POWER shows is the processor's
        // power unit, so it belongs to Processor. J2: the PSU sheet shows parts of PSU and of its four children, and J3: the
        // root sheet shows no owned part; for those the worker pauses and the person binds them (J2 to PSU, J3 to System). The
        // worker may see the three placements in one or more rounds; every round's requests are answered the same way.
        var chosen = new Dictionary<Guid, int> { [j2Component] = 2, [j3Component] = 1 };
        var offered = new Dictionary<Guid, Guid[]> { [j2Component] = [Block(1), Block(2), Block(4), Block(5), Block(6), Block(7)], [j3Component] = [] };
        var answered = new List<Guid>();
        bool AllAdopted(SchematicDesign d) => new[] { j2Component, j3Component, j4Component }.All(id => d.Engineering.Circuit.Components.Any(c => c.Id == id));
        ulong answeredAt = 0; int round = 0;
        while (true)
        {
            var settled = await Worker("native additions", s => (Phase(s) == "Watching" && Settled(AllAdopted))
                || (Phase(s) == "Paused" && Code(s) == BlockOwnershipSynchronization.ResolutionRequired && s.GetProperty("sequence").GetUInt64() > answeredAt));
            if (Phase(settled) == "Watching") break;
            Assert.IsLessThanOrEqualTo(3, ++round, "Three placements need at most three rounds.");
            var requests = (await BlockPlan("block-plan-additions-" + round)).GetProperty("resolutionRequests").EnumerateArray().ToArray();
            Assert.AreNotEqual(0, requests.Length, "The worker paused for owners, so the plan names them.");
            foreach (var request in requests)
            {
                Guid component = request.GetProperty("componentId").GetGuid();
                Assert.IsTrue(chosen.ContainsKey(component), $"Only J2 and J3 need the person; {component} was never meant to be asked about.");
                Assert.AreEqual(BlockOwnershipSynchronization.OwnerUnresolved, request.GetProperty("code").GetString());
                CollectionAssert.AreEqual(offered[component], request.GetProperty("candidateBlockIds").EnumerateArray().Select(e => e.GetGuid()).ToArray(),
                    "The request offers every block from the root down to each owner of the sheet's other parts, and nothing on the root sheet.");
                await BindTo(component, Block(chosen[component]), "owner-choice-" + round + "-" + (component == j2Component ? "psu" : "root"));
                answered.Add(component);
            }
            answeredAt = sequence;
            RequireToolSuccess(await host.Tool("kicad_design_automatic_sync_resume", new { instanceId, sessionId, expectedSequence = sequence }));
        }
        CollectionAssert.AreEquivalent(new[] { j2Component, j3Component }, answered, "The person answered J2 and J3, once each.");
        var owners = await BlockPlan("block-plan-additions-owned");
        Assert.AreEqual(0, owners.GetProperty("assignments").GetArrayLength());
        Assert.AreEqual(0, owners.GetProperty("resolutionRequests").GetArrayLength());

        var adopted = await Published();
        var j2Instance = adopted.Engineering.Circuit.Components.Single(c => c.Id == j2Component);
        Assert.AreEqual("J2", j2Instance.Reference);
        Assert.AreEqual(PsuCpuIds.Id(0x05, 2), j2Instance.SheetInstanceId, "It sits on the PSU sheet instance KiCad shows it on.");
        Assert.AreEqual(PsuCpuIds.Id(0x05, 4), adopted.Engineering.Circuit.Components.Single(c => c.Id == j4Component).SheetInstanceId);
        foreach (var component in new[] { j2Component, j3Component, j4Component })
            Assert.AreEqual(PsuCpuIds.Id(0x03, 1), adopted.Engineering.Circuit.Sheets.SelectMany(s => s.Components)
                .Single(d => d.Id == adopted.Engineering.Circuit.Components.Single(c => c.Id == component).DefinitionId).PartId,
                "Its part is the fixture's connector: the same library symbol with exactly the same pins.");
        Assert.AreEqual(SchematicDesignBindings.PathKey(NativePathOf(PsuCpuIds.Id(0x05, 2))),
            SchematicDesignBindings.PathKey(adopted.SheetBindings.Single(b => b.SheetInstanceId == j2Instance.SheetInstanceId).NativePath));
        Assert.AreEqual(j2Native, adopted.SymbolBindings.Single(b => b.SymbolOccurrenceId == j2Occurrence).NativeObjectId);
        Assert.AreEqual(j4Native, adopted.SymbolBindings.Single(b => b.SymbolOccurrenceId == j4Occurrence).NativeObjectId);
        var placed = (await Capture()).Electrical.Hierarchy.Data;
        Assert.IsTrue(SchematicOrientation.Equivalent(SchematicModelProjection.Placement(NativeOf(adopted, placed, j2Occurrence)),
            adopted.Engineering.Circuit.Symbols.Single(s => s.Id == j2Occurrence).Placement!));
        Assert.IsFalse(adopted.Engineering.Circuit.Nets.Any(n => n.Pins.Any(p => p.ComponentId == j2Component || p.ComponentId == j4Component)),
            "Their unconnected pins make no net.");
        var afterAddition = await Blocks();
        RecursiveBlockRevision Owner(Guid component) => afterAddition.Walk(afterAddition.SelectedRoot).Select(afterAddition.Inspect)
            .Single(r => r.EffectiveComponentBindings.Targets.Contains(new ComponentRealization(designId, circuitId, component)));
        Assert.AreEqual(Block(8), Owner(j4Component).Selection.BlockId, "Processor owns the connector placed beside its power unit.");
        Assert.AreEqual(RequirementRevisionActor.Editor, Owner(j4Component).Origin.ActorKind, "The worker bound it, as an edit made in KiCad.");
        Assert.AreEqual(Block(2), Owner(j2Component).Selection.BlockId, "PSU owns J2, as the person chose.");
        Assert.AreEqual(Block(1), Owner(j3Component).Selection.BlockId, "System owns J3, as the person chose.");
        var initialGraph = PsuCpuFixture.Graph();
        Assert.AreNotEqual(initialGraph.SelectedRoot, afterAddition.SelectedRoot, "A new root snapshot selects the new revisions.");
        Assert.AreEqual(initialGraph.Walk(initialGraph.SelectedRoot).Single(s => s.BlockId == Block(8)).RevisionId,
            afterAddition.Revisions.Single(r => r.Selection.BlockId == Block(8) && r.EffectiveComponentBindings.Targets.Any(t => t.ComponentId == j4Component)).ParentRevisionId,
            "The new Processor revision follows the one the fixture selected.");
        foreach (int unchanged in new[] { 4, 5, 6, 7, 9 })
            Assert.AreEqual(initialGraph.Walk(initialGraph.SelectedRoot).Single(s => s.BlockId == Block(unchanged)),
                afterAddition.Walk(afterAddition.SelectedRoot).Single(s => s.BlockId == Block(unchanged)), "Other blocks keep their revisions.");
        string blocksAfterAddition = await BlocksSha();
        Assert.IsFalse((await BlockApply("addition-block-repeat")).GetProperty("blockGraphWritten").GetBoolean(), "Repeating block ownership writes nothing.");
        Assert.AreEqual(blocksAfterAddition, await BlocksSha());
        Step("native additions owned", new { j2 = j2Component, j3 = j3Component, j4 = j4Component, rounds = round });

        // ---- 3. An instruction on J4 survives undo and redo in KiCad --------------------------------------------------
        var statement = new EngineeringStatement(Guid.NewGuid(), j4Component, EngineeringStatementRole.Intent, GuidanceStrength.Requirement,
            "Keep this connector beside the processor's power pins.", null, [], []);
        var current = store.Read()!.State.Baseline;
        await SaveXml(current with { Engineering = current.Engineering with { Structure = current.Engineering.Structure with
            { Statements = [.. current.Engineering.Structure.Statements, statement] } } });
        await Worker("instruction", s => Phase(s) == "Watching" && Settled(d => d.Engineering.Structure.Statements.Any(x => x.Id == statement.Id)));
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        await Worker("native undo", s => Phase(s) == "Watching" && Settled(d => !d.Engineering.Circuit.Components.Any(c => c.Id == j4Component)));
        var undone = await Published();
        Assert.IsTrue(undone.Engineering.Circuit.Components.Any(c => c.Id == j2Component), "Undo takes back only the last placement.");
        Assert.AreEqual(statement, undone.Engineering.Structure.Statements.Single(s => s.Id == statement.Id), "The instruction is kept.");
        Assert.IsTrue(undone.Engineering.Structure.UnresolvedComponentReferences!.Any(r => r.OwnerId == statement.Id
            && r.Slot == ComponentReferenceSlot.StatementTarget && r.FormerTarget.ComponentId == j4Component), "It is kept detached from the removed connector.");
        var detached = await BlockPlan("block-plan-undone");
        CollectionAssert.Contains(detached.GetProperty("detachedComponents").EnumerateArray().Select(e => e.GetGuid()).ToArray(), j4Component,
            "The Processor binding stays, detached, while the connector is gone.");
        Assert.AreEqual(blocksAfterAddition, await BlocksSha(), "Undo writes no block revision.");
        await FocusedSchematicShortcut(client, document, processId, display, "y", token);
        await Worker("native redo", s => Phase(s) == "Watching" && Settled(d => d.Engineering.Circuit.Components.Any(c => c.Id == j4Component)));
        var redone = await Published();
        Assert.AreEqual(j4Native, redone.SymbolBindings.Single(b => b.SymbolOccurrenceId == j4Occurrence).NativeObjectId, "Redo restores the same identities.");
        Assert.IsFalse(redone.Engineering.HasUnresolvedComponentReferences, "The instruction is attached again.");
        Assert.AreEqual(j4Component, redone.Engineering.Structure.Statements.Single(s => s.Id == statement.Id).TargetId);
        Assert.AreEqual(0, (await BlockPlan("block-plan-redone")).GetProperty("detachedComponents").GetArrayLength());
        Assert.AreEqual(blocksAfterAddition, await BlocksSha(), "Redo writes no block revision either.");
        Step("undo and redo in KiCad");

        // ---- 4. An undecidable part: two parts drawn or declared with the same library resistor -----------------------
        var r5 = Place(r1, "R2", psu, Grid(free.LeftNm + 127_000_000), Grid(free.BottomNm + 20_320_000));
        byte[] beforeAmbiguous = await File.ReadAllBytesAsync(path, token);
        await NativeBatch("Place a resistor in KiCad", psu, new SchematicItemOperation { Create = Any.Pack(r5) });
        var ambiguous = await Worker("undecidable part", s => Phase(s) == "Paused" && Code(s) == SchematicNativeAdditionProjection.ResolutionRequired);
        var preview = await host.Tool("kicad_design_sync_plan", Recovery());
        await File.WriteAllTextAsync(Evidence("undecidable-part-plan.json"), RetainedToolEvidence(preview), token);
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, Error(preview), preview.GetRawText());
        var partRequest = preview.GetProperty("structuredContent").GetProperty("ownershipResolutionRequests").EnumerateArray().Single();
        Assert.AreEqual(SchematicNativeAdditionProjection.PartAmbiguous, partRequest.GetProperty("Code").GetString());
        Assert.AreEqual(Guid.Parse(r5.Id.Value), partRequest.GetProperty("NativeObjectId").GetGuid());
        CollectionAssert.AreEquivalent(new[] { fixtureR.Id, sense.Id }, partRequest.GetProperty("CandidatePartIds").EnumerateArray().Select(e => e.GetGuid()).ToArray());
        CollectionAssert.AreEqual(beforeAmbiguous, await File.ReadAllBytesAsync(path, token), "Nothing is published while the part is undecided.");
        Assert.IsTrue((await Capture()).Electrical.Hierarchy.Data.Instances.SelectMany(s => s.Items).Any(i => i.Is(SchematicSymbolInstance.Descriptor)
            && i.Unpack<SchematicSymbolInstance>().Id.Value == r5.Id.Value), "KiCad keeps the person's symbol.");
        // The person takes the symbol back; the paused worker is stopped and the next steps use the public plan and apply.
        var stopped = await host.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId });
        RequireToolSuccess(stopped);
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        RequireToolSuccess(await host.Tool("kicad_design_recovery_refresh", Recovery()));
        Assert.IsFalse(store.Read()!.State.Observed.Instances.SelectMany(x => x.Items).Any(i => i.Is(SchematicSymbolInstance.Descriptor)
            && i.Unpack<SchematicSymbolInstance>().Id.Value == r5.Id.Value), "Undo removed the undecided symbol.");
        CollectionAssert.AreEqual(beforeAmbiguous, await File.ReadAllBytesAsync(path, token));
        await NoOpPlan("undecided-undone-plan");
        Step("undecidable part paused", new { code = Code(ambiguous), candidates = new[] { fixtureR.Id, sense.Id } });

        // ---- 5. The XML removes a unit and a component; the public plan and apply remove them in KiCad ----------------
        current = store.Read()!.State.Baseline;
        Guid unitFour = PsuCpuIds.Id(0x09, 10), memory = PsuCpuIds.Id(0x09, 11);
        var removedNative = new[] { unitFour, memory }.Select(o => current.SymbolBindings.Single(b => b.SymbolOccurrenceId == o).NativeObjectId.ToString("D")).ToArray();
        saved = await Desire(store.Read()!, SchematicRebuildTests.WithoutOccurrences(current, unitFour, memory));
        var beforeRemoval = await Capture();
        var removalPlan = await Plan("removal-plan");
        CollectionAssert.AreEquivalent(removedNative, Operations(removalPlan).Where(o => o.Remove is not null).Select(o => o.Remove.Value).ToArray(),
            "The preview removes exactly the two symbols.");
        Assert.IsTrue(Operations(removalPlan).Where(o => o.Remove is null).All(o => o.ReplaceLibraryCache is not null),
            "Besides the removals, the preview only keeps the touched sheets' library caches as they are.");
        CollectionAssert.AreEquivalent(new[] { unitFour, memory },
            removalPlan.GetProperty("removedSymbolOccurrences").EnumerateArray().Select(e => e.GetGuid()).ToArray());
        Assert.AreEqual(beforeRemoval, await Capture(), "Planning does not change KiCad.");
        await Apply("removal", mutation: true);
        var removed = await Capture();
        var shown = removed.Electrical.Hierarchy.Data.Instances.SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value).ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(removedNative.Any(shown.Contains), "KiCad no longer draws the removed unit and component.");
        CollectionAssert.AreEqual(new[] { j4.Id.Value }, removed.Electrical.Hierarchy.Data.Instances.Single(s => SheetPathKey(s.Metadata.Document) == sheetPaths["CPU_POWER"]).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value).ToArray(),
            "CPU_POWER shows only the connector placed beside the removed unit.");
        Assert.IsTrue(store.Read()!.State.Baseline.Engineering.Circuit.Components.Any(c => c.Id == PsuCpuIds.Id(0x07, 7)), "U5 keeps its other units.");
        var afterRemoval = await BlockPlan("block-plan-removed");
        CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x07, 8) },
            afterRemoval.GetProperty("detachedComponents").EnumerateArray().Select(e => e.GetGuid()).ToArray(), "The Memory binding stays, detached.");
        string blocksBeforeRepeat = await BlocksSha();
        Assert.IsFalse((await BlockApply("removal-block-repeat")).GetProperty("blockGraphWritten").GetBoolean());
        Assert.AreEqual(blocksBeforeRepeat, await BlocksSha());
        await ApplyUnchanged("removal-repeat");
        Step("XML removal applied in KiCad", new { removedNative });

        // ---- 6. Simultaneous conflicting edits of one component keep both versions and pause -------------------------
        // The person moves U3 (the PSU's LTC2959) in KiCad while the XML removes U3. Applying the removal would discard the
        // move, and publishing the move would bring U3 back into the XML, so the worker keeps both versions and pauses.
        current = store.Read()!.State.Baseline;
        Guid u3 = PsuCpuIds.Id(0x09, 5);
        Guid u3Component = current.Engineering.Circuit.Symbols.Single(s => s.Id == u3).ComponentId;
        Assert.AreEqual(PsuCpuIds.Id(0x05, 2), current.Engineering.Circuit.Components.Single(c => c.Id == u3Component).SheetInstanceId, "U3 sits on the PSU sheet.");
        string u3Native = current.SymbolBindings.Single(b => b.SymbolOccurrenceId == u3).NativeObjectId.ToString("D");
        var shownBeforeConflict = (await Capture()).Electrical.Hierarchy.Data;
        var movedU3 = NativeOf(current, shownBeforeConflict, u3);
        Assert.AreEqual(u3Native, movedU3.Id.Value);
        const long nudge = 1_270_000;
        movedU3.Position.XNm += nudge;
        foreach (var field in new[] { movedU3.ReferenceField, movedU3.ValueField, movedU3.FootprintField, movedU3.DatasheetField,
                     movedU3.DescriptionField }.Concat(movedU3.UserFields))
            if (field?.Text?.Position is { } fieldPosition) fieldPosition.XNm += nudge;
        await NativeBatch("Move U3 one grid step in KiCad", psu, new SchematicItemOperation { Update = Any.Pack(movedU3) });
        var withoutU3 = SchematicRebuildTests.WithoutOccurrences(current, u3);
        byte[] xmlVersion = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(withoutU3, []));
        await File.WriteAllBytesAsync(path, xmlVersion, token);
        var restarted = await host.Tool("kicad_design_automatic_sync_start", new { instanceId, recoveryPath = store.StatePath, designPath = path,
            expectedRecoveryRevision = store.Read()!.RevisionToken, blockGraphPath = blocksPath, designId = designId.ToString("D") });
        RequireToolSuccess(restarted);
        sessionId = restarted.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
        sequence = restarted.GetProperty("structuredContent").GetProperty("status").GetProperty("sequence").GetUInt64();
        var conflict = await Worker("simultaneous edits", s => Phase(s) == "Paused" && Code(s) == "ownership_change_with_xml_edits");
        CollectionAssert.AreEqual(xmlVersion, await File.ReadAllBytesAsync(path, token), "The XML version is kept.");
        static IReadOnlyDictionary<string, SchematicSymbolInstance> NativeSymbols(CheckedSchematicState state) => state.Electrical.Hierarchy.Data.Instances
            .SelectMany(s => s.Items).Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .ToDictionary(x => x.Id.Value, StringComparer.Ordinal);
        var nativeVersion = NativeSymbols(await Capture());
        Assert.AreEqual(movedU3.Position, nativeVersion[u3Native].Position, "KiCad's version is kept: U3 stays where the person moved it.");
        Assert.IsTrue(store.Read()!.State.Baseline.Engineering.Circuit.Symbols.Any(x => x.Id == u3), "Nothing was published: the design still holds U3.");
        Assert.IsFalse(store.Read()!.State.HasPendingWork);
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        RequireToolSuccess(await host.Tool("kicad_design_automatic_sync_resume", new { instanceId, sessionId, expectedSequence = sequence }));
        await Worker("conflict resolved in KiCad", s => Phase(s) == "Watching" && Settled(d => !d.Engineering.Circuit.Symbols.Any(x => x.Id == u3)));
        var resolved = NativeSymbols(await Capture());
        Assert.IsFalse(resolved.ContainsKey(u3Native), "After the move is undone, the XML removal of U3 reached KiCad.");
        Assert.IsTrue(resolved.ContainsKey(j2Native.ToString("D")) && resolved.ContainsKey(j4Native.ToString("D")), "Every other symbol stays.");
        var resolvedDesign = await Published();
        Assert.IsFalse(resolvedDesign.Engineering.Circuit.Components.Any(c => c.Id == u3Component), "The XML version won once KiCad's edit was undone.");
        CollectionAssert.AreEquivalent(new[] { PsuCpuIds.Id(0x07, 8), u3Component },
            (await BlockPlan("block-plan-conflict-resolved")).GetProperty("detachedComponents").EnumerateArray().Select(e => e.GetGuid()).ToArray(),
            "The Memory and U3 bindings stay, detached.");
        Step("conflict kept both versions", new { code = Code(conflict), sameComponent = u3Component });

        // ---- 7. Every repeat is a no-op --------------------------------------------------------------------------------
        RequireToolSuccess(await host.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId }));
        RequireToolSuccess(await host.Tool("kicad_design_recovery_refresh", Recovery()));
        await NoOpPlan("final-plan");
        await ApplyUnchanged("final-repeat");
        string finalBlocks = await BlocksSha();
        var finalBlockApply = await BlockApply("final-block-repeat");
        Assert.IsFalse(finalBlockApply.GetProperty("blockGraphWritten").GetBoolean());
        Assert.AreEqual(finalBlocks, await BlocksSha());
        var final = await Blocks();
        var finalDesign = await Published();
        foreach (var component in finalDesign.Engineering.Circuit.Components)
            Assert.AreEqual(1, final.Walk(final.SelectedRoot).Count(s => final.Inspect(s).EffectiveComponentBindings.Targets.Any(t => t.ComponentId == component.Id)),
                $"Ownership totality: {component.Reference} is owned by exactly one block.");
        Step("done");
        await File.WriteAllTextAsync(Evidence("proof.json"), JsonSerializer.Serialize(new
        {
            instanceId, fixture = "psu-cpu", PsuCpuFixture.Version, seed = context.Seed.ToString(), realStdioProductionServer = true, steps, statuses,
            additions = new
            {
                cpuPower = new { component = j4Component, occurrence = j4Occurrence, native = j4Native, part = PsuCpuIds.Id(0x03, 1), owner = "Processor",
                    boundBy = "automatic worker", newRootSnapshot = true },
                psu = new { component = j2Component, pausedWith = BlockOwnershipSynchronization.ResolutionRequired,
                    candidates = offered[j2Component], chosenBlock = "PSU" },
                root = new { component = j3Component, pausedWith = BlockOwnershipSynchronization.ResolutionRequired, candidates = offered[j3Component],
                    chosenBlock = "System" },
                rounds = round
            },
            undoRedo = new { instructionDetachedOnUndo = true, instructionAttachedOnRedo = true, sameIdentitiesAfterRedo = true, blockBindingKept = true },
            undecidablePart = new { pausedWith = SchematicNativeAdditionProjection.ResolutionRequired, candidates = new[] { fixtureR.Id, sense.Id }, published = false },
            xmlRemoval = new { removedNative, previewExact = true, appliedInKiCad = true, bindingDetached = true },
            conflict = new { pausedWith = "ownership_change_with_xml_edits", component = u3Component, kicadEdit = "moved one grid step",
                xmlEdit = "removed", xmlVersionKept = true, kicadVersionKept = true, resolvedAfterUndo = true },
            repeatsNoOp = true, crossPlatformReady = false,
            remaining = new[]
            {
                "Sheets inserted, removed or moved to another parent in KiCad or in the XML are not yet reflected on the other side (sheet_ownership_changed).",
                "A part chosen for an undecidable new symbol (part_ambiguous) cannot be applied yet.",
                "Answers to unit-owner and unit-grouping requests for new units of multi-unit parts cannot be applied yet.",
                "Part and pin rebinding of an existing component has no explicit resolution request yet.",
                "A symbol placed on a sheet shown several times is refused (native_addition_repeated_sheet_unsupported).",
                "A KiCad undo that restores symbols in the same change as new placements is refused (native_restoration_with_additions).",
                "A symbol placed before the first synchronization is refused (missing_native_ownership_history) until the planning seam plans a never-synchronized design with empty history; a design synchronized without content-verified retained XML stays refused (unverified_native_ownership_history).",
                "Independent edits of different components made in KiCad and the XML at once are refused like conflicting ones (ownership_change_with_xml_edits) rather than merged; this journey exercises only the conflicting pair.",
                "Two designs sharing one block graph cannot both run block-keeping workers (automatic_sync_ownership_conflict).",
                "A block graph edited outside the worker is not re-settled until the design or KiCad changes.",
                "XML removal of a connected unit is proven offline only; this journey removes units on the Components stage, which has no nets.",
                "Symbols are placed through the native item-batch commit, not through KiCad's Add Symbol or Duplicate tools (unannotated references such as J? are not exercised)."
            }
        }), token);

        string Phase(JsonElement status) => status.GetProperty("phase").GetString()!;
        string? Code(JsonElement status) => status.GetProperty("errorCode").GetString();
        bool Settled(Func<SchematicDesign, bool> done) => store.Read() is { } record && !record.State.HasPendingWork && done(record.State.Baseline);

        // Waits for the worker's statuses until one satisfies reached, starting with the one already seen (the worker may have
        // settled before the call); a new pause or stop fails with its status. The status already seen is only a starting
        // point: a pause seen before resuming is not a new pause.
        async Task<JsonElement> Worker(string what, Func<JsonElement, bool> reached)
        {
            ulong known = sequence, after = sequence == 0 ? 0 : sequence - 1;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var call = host.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId, afterSequence = after });
                if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(Math.Max(1, 120 - deadline.Elapsed.TotalSeconds)), token)) != call)
                    throw new AssertFailedException($"{what}: the worker did not settle within 120 s; last sequence {sequence}.");
                var next = await call; RequireToolSuccess(next);
                var status = next.GetProperty("structuredContent").GetProperty("status").Clone();
                sequence = after = status.GetProperty("sequence").GetUInt64();
                statuses.Add(new { what, sequence, phase = Phase(status), code = Code(status), seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
                if (reached(status)) return status;
                if (sequence > known && Phase(status) is "Paused" or "Stopped" or "InvalidDesign")
                    throw new AssertFailedException($"{what}: the worker stopped at {status.GetRawText()}");
            }
        }

        SchematicScreenData Screen(Guid modelSheet, SchematicHierarchyData data) => data.Instances.Single(s => SheetPathKey(s.Metadata.Document)
            == SchematicDesignBindings.PathKey(store.Read()!.State.Baseline.SheetBindings.Single(b => b.SheetInstanceId == modelSheet).NativePath));
        DocumentSpecifier Sheet(Guid modelSheet) => Screen(modelSheet, store.Read()!.State.Baseline.Schematic).Metadata.Document.Clone();

        async Task NativeBatch(string description, DocumentSpecifier on, params SchematicItemOperation[] operations)
        {
            var batch = new ApplySchematicItemBatch { Document = on.Clone(), Description = description };
            batch.Operations.Add(operations);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }

        async Task SaveXml(SchematicDesign design) =>
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [])), token);

        async Task<StoredDesignRecovery> Desire(StoredDesignRecovery at, SchematicDesign design)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));
            await File.WriteAllBytesAsync(path, bytes, token);
            return store.Save(at.State with { DesiredFileBytes = bytes }, at.RevisionToken);
        }

        async Task<JsonElement> Plan(string name)
        {
            var plan = await host.Tool("kicad_design_sync_plan", Recovery());
            await File.WriteAllTextAsync(Evidence(name + ".json"), RetainedToolEvidence(plan), token);
            RequireToolSuccess(plan);
            var content = plan.GetProperty("structuredContent").Clone();
            Assert.IsTrue(content.GetProperty("canPrepare").GetBoolean(), plan.GetRawText());
            return content;
        }

        async Task NoOpPlan(string name)
        {
            var content = await Plan(name);
            Assert.AreEqual(0, content.GetProperty("nativeOperationsJson").GetArrayLength(), name + ": nothing would reach KiCad.");
            Assert.AreEqual(JsonValueKind.Null, content.GetProperty("addedComponents").ValueKind, name + ": nothing new to adopt.");
            Assert.AreEqual(JsonValueKind.Null, content.GetProperty("ownershipResolutionRequests").ValueKind);
        }

        async Task Apply(string name, bool mutation)
        {
            var args = new { instanceId, recoveryPath = store.StatePath, designPath = path, expectedRevisionToken = store.Read()!.RevisionToken,
                operationId = Guid.NewGuid().ToString("D") };
            var applied = await host.Tool("kicad_design_sync_apply", args);
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), RetainedToolEvidence(applied), token);
            RequireToolSuccess(applied);
            var result = applied.GetProperty("structuredContent");
            Assert.AreEqual(mutation, result.GetProperty("nativeMutationCommitted").GetBoolean(), applied.GetRawText());
            Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), applied.GetRawText());
            var replay = await host.Tool("kicad_design_sync_apply", args); RequireToolSuccess(replay);
            Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(), "The same apply replays exactly.");
            Assert.IsFalse(store.Read()!.State.HasPendingWork);
        }

        async Task ApplyUnchanged(string name)
        {
            var before = await Capture();
            byte[] file = await File.ReadAllBytesAsync(path, token);
            var applied = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath = path,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), RetainedToolEvidence(applied), token);
            RequireToolSuccess(applied);
            var result = applied.GetProperty("structuredContent");
            Assert.IsFalse(result.GetProperty("nativeMutationCommitted").GetBoolean(), applied.GetRawText());
            Assert.IsFalse(result.GetProperty("nativeFilesSaved").GetBoolean(), applied.GetRawText());
            Assert.AreEqual(before, await Capture(), name + ": KiCad is unchanged.");
            CollectionAssert.AreEqual(file, await File.ReadAllBytesAsync(path, token), name + ": the XML is unchanged.");
        }

        async Task<JsonElement> BlockPlan(string name)
        {
            var plan = await host.Tool("kicad_design_block_owners_plan", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, blockGraphPath = blocksPath, designId = designId.ToString("D") });
            await File.WriteAllTextAsync(Evidence(name + ".json"), RetainedToolEvidence(plan), token);
            RequireToolSuccess(plan);
            return plan.GetProperty("structuredContent").Clone();
        }

        async Task<JsonElement> BlockApply(string name)
        {
            var plan = await BlockPlan(name + "-plan");
            var applied = await host.Tool("kicad_design_block_owners_apply", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, blockGraphPath = blocksPath, designId = designId.ToString("D"),
                expectedBlockGraphSha256 = plan.GetProperty("blockGraphSha256").GetString() });
            await File.WriteAllTextAsync(Evidence(name + ".json"), RetainedToolEvidence(applied), token);
            RequireToolSuccess(applied);
            return applied.GetProperty("structuredContent").Clone();
        }

        // The person binds a component to the block they choose, through the per-level editor's public tool.
        async Task BindTo(Guid component, Guid block, string name)
        {
            var graph = await Blocks();
            var blockPath = new List<BlockSelection>();
            bool Visit(BlockSelection selection)
            {
                blockPath.Add(selection);
                if (selection.BlockId == block || graph.Inspect(selection).Children.Any(Visit)) return true;
                blockPath.RemoveAt(blockPath.Count - 1);
                return false;
            }
            Assert.IsTrue(Visit(graph.SelectedRoot), "The chosen block is in the selected design.");
            var session = await client.HandshakeAsync(token);
            var bound = await host.Tool("kicad_diagram_components_set", new
            {
                instanceId, expectedInstanceEpoch = session.Epoch, repositoryRoot = context.ProjectDirectory, sourcePath = blocksPath,
                documentId = documentId.ToString("D"), expectedSourceToken = await BlocksSha(),
                expectedRoot = JsonSerializer.SerializeToElement(graph.SelectedRoot, web),
                blockPath = JsonSerializer.SerializeToElement(blockPath, web),
                bindings = JsonSerializer.SerializeToElement(new BlockComponentBindings([.. graph.Inspect(blockPath[^1]).EffectiveComponentBindings.Targets,
                    new ComponentRealization(designId, circuitId, component)]), web),
                operationId = Guid.NewGuid(), actor = "PSU/CPU ownership journey"
            });
            await File.WriteAllTextAsync(Evidence(name + ".json"), bound.GetRawText(), token);
            RequireToolSuccess(bound);
        }

        IReadOnlyList<SchematicItemOperation> Operations(JsonElement plan) => [.. plan.GetProperty("nativeOperationsJson").EnumerateArray()
            .Select(o => SchematicJson.Parser.Parse<SchematicItemOperation>(o.GetString()!))];
    }

    // The native symbol an occurrence of the design is bound to, where the snapshot shows it.
    private static SchematicSymbolInstance NativeOf(SchematicDesign design, SchematicHierarchyData snapshot, Guid occurrence) =>
        SchematicModelProjection.NativeSymbols(design with { Schematic = snapshot }, snapshot)[occurrence].Clone();

    // A copy of a placed symbol as a person places it in KiCad: a new symbol UUID and new placed-pin UUIDs, the same library
    // symbol, the given reference, at an absolute position on the given sheet.
    private static SchematicSymbolInstance Place(SchematicSymbolInstance template, string reference, DocumentSpecifier sheet, long x, long y)
    {
        var symbol = SymbolSheetOwnershipTests.PlacedCopy(template, reference, x - template.Position.XNm, y - template.Position.YNm);
        symbol.Path = sheet.SheetPath.Clone();
        var record = new SymbolSheetRecord { ProjectName = sheet.Project.Name, Reference = reference, Unit = template.Unit.Unit, Variants = new() };
        record.Path.Add(sheet.SheetPath.Path.Select(p => p.Clone()));
        symbol.InstanceRecords = new();
        symbol.InstanceRecords.Records.Add(record);
        return symbol;
    }

    // Ledger pc97a1139c2e34c36. KiCad writes the project file's sheet list only when it saves, so a sheet renamed since the
    // last save leaves that list stale in the editor, as every sheet of a project whose file KiCad has not written yet does.
    // The apply's own save then rewrites the list: KiCad saves exactly the planned design, yet its full state digest differs
    // from the one recorded before the save. The apply must still publish, leave no pending work and replay exactly. The test
    // host pauses right after KiCad confirms the save, so the check reads the recorded pre-save state and KiCad's state after
    // saving, which proves publication accepted the save by the save-stable digest, then lets the same apply finish.
    private static async Task VerifyRenamedSheetPublishesThroughItsSave(NativeClient client, DocumentSpecifier root,
        DocumentSpecifier sheet, string projectDirectory, DesignRecoveryStore store, string designPath, string evidence,
        string instanceId, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(120));
        string projectFile = Path.Combine(projectDirectory, "fixture.kicad_pro");
        string sheetId = sheet.SheetPath.Path[^1].Value;
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, limit.Token);
        // The names the saved project file lists for this sheet: [uuid, name] pairs under "sheets".
        async Task<string[]> SavedNames()
        {
            using var project = JsonDocument.Parse(await File.ReadAllBytesAsync(projectFile, limit.Token));
            return project.RootElement.TryGetProperty("sheets", out var sheets) ? [.. sheets.EnumerateArray()
                .Where(e => e.GetArrayLength() == 2 && e[0].GetString() == sheetId).Select(e => e[1].GetString()!)] : [];
        }
        static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

        var before = await Capture();
        Assert.IsFalse(before.State.NativeContentDirty, "The rename starts from a saved project.");
        var symbol = before.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Value == sheetId);
        string oldName = symbol.NameField.Text.Text_, newName = oldName + " renamed";
        CollectionAssert.AreEqual(new[] { oldName }, await SavedNames(), "The saved sheet list names the sheet as KiCad last saved it.");
        var renamed = symbol.Clone(); renamed.NameField.Text.Text_ = newName;
        var rename = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Rename a sheet" };
        rename.Operations.Add(new SchematicItemOperation { Update = Any.Pack(renamed) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rename, limit.Token);
        Assert.IsTrue((await Capture()).State.NativeContentDirty, "The rename is unsaved work in the editor.");
        CollectionAssert.AreEqual(new[] { oldName }, await SavedNames(), "Renaming alone does not write the project file.");
        var saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, limit.Token, includeElectrical: true);
        byte[] previousXml = await File.ReadAllBytesAsync(designPath, limit.Token);

        Guid operationId = Guid.NewGuid();
        var args = new { instanceId, recoveryPath = store.StatePath, designPath, expectedRevisionToken = saved.RevisionToken,
            operationId = operationId.ToString("D") };
        string name = instanceId + "-renamed-sheet";
        string marker = Path.Combine(evidence, name + "-saved.json"), release = Path.Combine(evidence, name + "-release");
        var start = SyncHarnessProcessTests.StartInfo("native-save", marker);
        start.Environment["KICAD_SYNC_HARNESS_PAUSE_RELEASE"] = release;
        await using var host = await StdioMcpFixture.StartAsync(start, Path.Combine(evidence, name + "-host"),
            Path.Combine(evidence, name + "-host.log"), limit.Token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        Task markerReady = SyncHarnessProcessTests.WaitForMarkerAsync(marker, limit.Token);
        Task<JsonElement> call = host.Tool("kicad_design_sync_apply", args);
        DocumentLifecycleState expected, afterSave;
        try
        {
            if (await Task.WhenAny(markerReady, call) == call)
            { RequireToolSuccess(await call); Assert.Fail("The apply finished without saving through KiCad."); }
            await markerReady;
            expected = store.Read()!.State.PendingNativeSave?.ExpectedState
                ?? throw new AssertFailedException("The apply records the state it asks KiCad to save.");
            afterSave = (await Capture()).State;
            Assert.IsFalse(afterSave.NativeContentDirty, "KiCad saved the rename.");
            Assert.IsTrue(afterSave.ProjectSettingsIncluded);
            CollectionAssert.AreEqual(new[] { newName }, await SavedNames(), "KiCad's save rewrote the stale sheet list.");
            Assert.AreNotEqual(expected.StateSha256, afterSave.StateSha256,
                "Rewriting the sheet list changes the full state digest, so this save is accepted only by the save-stable digest.");
            Assert.IsTrue(IsDigest(expected.SaveStableStateSha256), "KiCad reports the save-stable digest before saving.");
            Assert.AreEqual(expected.SaveStableStateSha256, afterSave.SaveStableStateSha256,
                "Apart from the entries a save derives from the schematic, KiCad saved exactly the recorded state.");
            await File.WriteAllBytesAsync(release, [], limit.Token);
        }
        catch
        {
            await host.TerminateAsync();
            try { await call; } catch (Exception) { }
            throw;
        }
        var applied = await call; RequireToolSuccess(applied);
        var data = applied.GetProperty("structuredContent");
        Assert.IsTrue(data.GetProperty("synchronizationCommitted").GetBoolean(), data.GetRawText());
        Assert.IsTrue(data.GetProperty("nativeFilesSaved").GetBoolean(), data.GetRawText());
        Assert.IsFalse(data.GetProperty("nativeMutationCommitted").GetBoolean(), "The rename was KiCad's; the apply sends no edit.");
        Assert.IsFalse(data.GetProperty("replayed").GetBoolean());
        Assert.AreEqual(JsonValueKind.String, data.GetProperty("previousXmlPath").ValueKind, "The candidate replaced the XML: it was published.");
        var completed = store.Read()!;
        Assert.IsFalse(completed.State.HasPendingWork, "Publication leaves no pending work.");
        Assert.AreEqual(operationId, completed.State.LastSynchronization!.OperationId);
        CollectionAssert.AreEqual(previousXml, await RetainedXmlHistory.ReadVerifiedAsync(completed.State.LastSynchronization!, limit.Token));
        var final = await Capture();
        Assert.AreEqual(afterSave, final.State, "Publishing the XML leaves KiCad's saved state untouched.");
        var published = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, limit.Token), []);
        Assert.AreEqual(newName, published.Schematic.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Value == sheetId).NameField.Text.Text_);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(final.Electrical.Hierarchy.Data, published.Schematic, limit.Token));
        var replay = await host.Tool("kicad_design_sync_apply", args); RequireToolSuccess(replay);
        Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
        Assert.AreEqual(final, await Capture(), "The exact replay changes nothing.");
        await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), JsonSerializer.Serialize(new
        {
            instanceId, sheet = sheetId, oldName, newName, operationId,
            recordedBeforeSave = new { expected.StateSha256, expected.SaveStableStateSha256 },
            afterSave = new { afterSave.StateSha256, afterSave.SaveStableStateSha256 },
            projectSheetListRewritten = true, fullDigestChanged = true, saveStableDigestKept = true,
            published = true, pendingWork = false, exactReplay = true
        }), token);
    }

    private static async Task VerifyNativeSymbolSheetOwnership(NativeClient client, DocumentSpecifier root,
        HierarchyFixture hierarchy, string evidence, string instanceId, int processId, string display, CancellationToken token)
    {
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = hierarchy.Second });
        var before = await Capture(); var baseline = ProbeElectricalModel(before.Electrical);
        var template = before.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .First(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        string[] libraryPins = [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")];
        string[] nativeIds = [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")];
        SchematicSymbolInstance Symbol(int index, DocumentSpecifier document, int unit, long x)
        {
            var symbol = template.Clone(); symbol.Id.Value = nativeIds[index];
            long dx = x - symbol.Position.XNm, dy = 150000000 - symbol.Position.YNm;
            foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField,
                symbol.DatasheetField, symbol.DescriptionField }.Concat(symbol.UserFields))
                if (field?.Text?.Position is { } position) { position.XNm += dx; position.YNm += dy; }
            symbol.Position = new() { XNm = x, YNm = 150000000 }; symbol.Path = document.SheetPath.Clone();
            symbol.Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 }; symbol.Locked = LockedState.LsUnlocked;
            symbol.Unit = new() { Unit = unit }; symbol.ReferenceField.Text.Text_ = index == 1 ? "U501" : "U500";
            symbol.ValueField.Text.Text_ = "Cross-sheet probe";
            symbol.Definition.Id.EntryName = "CrossSheetUnits"; symbol.Definition.UnitCount = 2;
            symbol.Definition.PinsUseLocalCoordinates = true; symbol.LibraryId = symbol.Definition.Id.Clone(); symbol.LibName = "";
            symbol.Definition.ReferenceField.Text.Text_ = "U"; symbol.Definition.ValueField.Text.Text_ = "Cross-sheet probe";
            var child = symbol.Definition.Items.First(i => i.Item.Is(SchematicPin.Descriptor)).Clone();
            foreach (var old in symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)).ToArray()) symbol.Definition.Items.Remove(old);
            for (int pinUnit = 1; pinUnit <= 2; pinUnit++)
            {
                var item = child.Clone(); var pin = item.Item.Unpack<SchematicPin>();
                pin.Id.Value = Guid.NewGuid().ToString("D"); pin.LibraryPinId = new() { Value = libraryPins[pinUnit - 1] };
                pin.Number = pinUnit.ToString(); pin.Name = "P" + pinUnit; pin.Position = new();
                item.Unit = new() { Unit = pinUnit }; item.Item = Any.Pack(pin); symbol.Definition.Items.Add(item);
            }
            symbol.InstanceRecords = new();
            foreach (var (path, reference) in index == 2 ? new[] { (first, "U500"), (second, "U501") } : new[] { (root, symbol.ReferenceField.Text.Text_) })
            {
                var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = reference, Unit = unit, Variants = new() };
                record.Path.Add(path.SheetPath.Path.Select(p => p.Clone())); symbol.InstanceRecords.Records.Add(record);
            }
            return symbol;
        }
        // The labels are written the way API clients and the XML synchronization write them. Only
        // some carry KiCad's hidden inter-sheet reference field (CN-1 §6.6 leaves it unset and
        // KiCad creates it); some carry custom fields, one visible and one hidden.
        (string Name, bool Visible)[] customFields = [("Signal note", true), ("Reviewer", false)];
        GlobalLabel Label(Vector2 position, bool referenceField = false, bool custom = false)
        {
            var label = new GlobalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = position.Clone(),
                Text = new() { Text_ = "CROSS_SHEET_LINK", Position = position.Clone(), Attributes = new()
                    { Size = new() { XNm = 1270000, YNm = 1270000 }, HorizontalAlignment = HorizontalAlignment.HaLeft,
                        VerticalAlignment = VerticalAlignment.VaCenter } }, Shape = SchematicLabelShape.SlshBidi, SpinStyle = SchematicLabelSpinStyle.SlssRight };
            SchematicField Field(string name, string value, bool isVisible, long dy)
            {
                var field = new SchematicField { Name = name, AllowAutoPlace = true, Visible = isVisible, Text = label.Text.Clone() };
                field.Text.Text_ = value; field.Text.Position.YNm += dy;
                // SCH_FIELD reloads with the native multiline text mode, unlike label text.
                field.Text.Attributes.Multiline = true;
                return field;
            }
            // As KiCad reports the field, placed above the label so the request stays recognizable.
            if (referenceField) label.IntersheetRefsField = Field("Intersheetrefs", "${INTERSHEET_REFS}", false, -2540000);
            if (custom)
                for (int index = 0; index < customFields.Length; index++)
                    label.Fields.Add(Field(customFields[index].Name, "Cross-sheet link " + customFields[index].Name.ToLowerInvariant(),
                        customFields[index].Visible, 2540000L * (index + 1)));
            return label;
        }
        var labels = new List<(DocumentSpecifier Sheet, GlobalLabel Sent)>();
        var rootBatch = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Cross-sheet component root units" };
        for (int index = 0; index < 2; index++)
        {
            var symbol = Symbol(index, root, 1, 150000000 + index * 30000000L);
            rootBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
            labels.Add((root, index == 0 ? Label(symbol.Position) : Label(symbol.Position, referenceField: true, custom: true)));
            rootBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(labels[^1].Sent) });
        }
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rootBatch, token);
        var shared = Symbol(2, first, 2, 150000000);
        var childBatch = new ApplySchematicItemBatch { Document = first.Clone(), Description = "Cross-sheet shared second unit" };
        childBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(shared) });
        labels.Add((first, Label(shared.Position, custom: true)));
        childBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(labels[^1].Sent) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(childBatch, token);
        await VerifyApiGlobalLabelFields();

        var initial = await Capture();
        Guid ModelSheet(DocumentSpecifier document) => baseline.SheetBindings.Single(b => SchematicDesignBindings.PathKey(b.NativePath)
            == string.Join('/', document.SheetPath.Path.Select(p => p.Value))).SheetInstanceId;
        Guid part = Guid.NewGuid(), definition = Guid.NewGuid(), connection = Guid.NewGuid();
        Guid[] components = [Guid.NewGuid(), Guid.NewGuid()];
        var instances = new[] { first, second };
        var occurrences = new List<SymbolOccurrence>(); var bindings = baseline.SymbolBindings.ToList();
        for (int index = 0; index < 2; index++)
        foreach (int unit in new[] { 1, 2 })
        {
            Guid id = Guid.NewGuid(); var document = unit == 1 ? root : instances[index]; string nativeId = unit == 1 ? nativeIds[index] : nativeIds[2];
            var native = initial.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document)).Items
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == nativeId);
            occurrences.Add(new(id, components[index], unit, SchematicModelProjection.Placement(native), unit == 1 ? ModelSheet(root) : null));
            bindings.Add(new(id, Guid.Parse(nativeId)));
        }
        Guid childDefinition = baseline.Engineering.Circuit.SheetInstances.Single(s => s.Id == ModelSheet(first)).DefinitionId;
        var circuit = baseline.Engineering.Circuit with
        {
            Parts = [.. baseline.Engineering.Circuit.Parts, new(part, "Cross-sheet two-unit device", 2, [new("1", "P1", 1), new("2", "P2", 2)])],
            Sheets = baseline.Engineering.Circuit.Sheets.Select(s => s.Id == childDefinition
                ? s with { Components = [.. s.Components, new(definition, part, "Cross-sheet probe")] } : s).ToArray(),
            Components = [.. baseline.Engineering.Circuit.Components, new(components[0], definition, ModelSheet(first), "U500"),
                new(components[1], definition, ModelSheet(second), "U501")],
            Symbols = [.. baseline.Engineering.Circuit.Symbols, .. occurrences],
            Nets = [.. baseline.Engineering.Circuit.Nets, new(connection, "Cross-sheet link", components.SelectMany(c => new[]
                { new PinEndpoint(c, "1"), new PinEndpoint(c, "2") }).ToArray())]
        };
        var removalInstructions = components.Select(c => new EngineeringStatement(Guid.NewGuid(), c, EngineeringStatementRole.Intent,
            GuidanceStrength.Requirement, "Keep this component instruction if its drawing is removed.", null, [], [])).ToArray();
        baseline = baseline with { Engineering = baseline.Engineering with { Circuit = circuit,
            Structure = baseline.Engineering.Structure with { Statements = [.. baseline.Engineering.Structure.Statements,
                .. removalInstructions, new(Guid.NewGuid(), connection, EngineeringStatementRole.Intent, GuidanceStrength.Requirement,
                    "Preserve the cross-sheet connection requirement when connectivity changes.", null, [], [])] } },
            Schematic = initial.Electrical.Hierarchy.Data.Clone(), SymbolBindings = bindings };
        var comparison = SchematicElectricalComparison.Compare(baseline, initial.Electrical, [], token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-sheets-initial-comparison.json"), JsonSerializer.Serialize(comparison), token);
        Assert.IsTrue(comparison.PinBindingsComplete); Assert.IsTrue(comparison.ConnectivityEquivalent); Assert.IsEmpty(comparison.UndrawnPins!);

        string designPath = Path.Combine(evidence, instanceId + "-symbol-sheets.xml");
        byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, [])); await File.WriteAllBytesAsync(designPath, xml, token);
        var store = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-symbol-sheets-recovery.json"));
        await using var planningHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-owner-plan-host"), Path.Combine(evidence, instanceId + "-owner-plan-host.log"), token);
        var restorationStops = new List<object>();
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(initial.State.Revision.Epoch, initial.State.Revision.Sequence),
            initial.Electrical.Hierarchy.TrackingComplete, baseline, xml, baseline.Schematic.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone()), null);
        await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = second }, token);
        var visible = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = second }, token);
        saved = store.Read()!; baseline = saved.State.Baseline;
        var target = occurrences.Single(o => o.ComponentId == components[0] && o.Unit == 1);
        var desired = baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
        {
            Components = baseline.Engineering.Circuit.Components.Select(c => c.Id == components[0] ? c with { Reference = "U550" } : c).ToArray(),
            Symbols = baseline.Engineering.Circuit.Symbols.Select(s => s.Id == target.Id ? s with
                { Placement = s.Placement! with { XMillimeters = s.Placement.XMillimeters + 2.54m } } : s).ToArray()
        } } };
        xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, [])); await File.WriteAllBytesAsync(designPath, xml, token);
        saved = store.Save(saved.State with { DesiredFileBytes = xml }, saved.RevisionToken);
        Guid operation = Guid.NewGuid();
        var result = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, operation, token);
        Assert.IsTrue(result.SynchronizationCommitted); Assert.IsTrue(result.NativeMutationCommitted);
        var changed = await Capture(); var parsed = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(changed.Electrical.Hierarchy.Data, parsed.Schematic, token));
        Assert.IsTrue(SchematicElectricalComparison.Compare(parsed, changed.Electrical, [], token).ConnectivityEquivalent);
        var actual = SchematicModelProjection.NativeSymbols(parsed, changed.Electrical.Hierarchy.Data);
        foreach (var unit in occurrences.Where(o => o.ComponentId == components[0])) Assert.AreEqual("U550", actual[unit.Id].ReferenceField.Text.Text_);
        foreach (var unit in occurrences.Where(o => o.ComponentId == components[1])) Assert.AreEqual("U501", actual[unit.Id].ReferenceField.Text.Text_);
        Assert.AreEqual(target.Placement!.XMillimeters + 2.54m, SchematicModelProjection.Placement(actual[target.Id]).XMillimeters);
        var afterView = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = second }, token);
        Assert.AreEqual(visible.Preview.Viewport, afterView.Preview.Viewport); Assert.AreEqual(visible.Preview.Png, afterView.Preview.Png);
        Assert.IsTrue((await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, operation, token)).Replayed);
        Assert.AreEqual(changed, await Capture());
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-symbol-sheets-visible.png"), afterView.Preview.Png.ToByteArray(), token);

        // Removing the shared second-unit drawing does not remove either
        // physical component or invent a placed pin for its undrawn unit.
        var remove = new ApplySchematicItemBatch { Document = first.Clone(), Description = "Undrawn symbol unit fixture" };
        remove.Operations.Add(new SchematicItemOperation { Remove = new() { Value = nativeIds[2] } });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
        var undrawn = await Capture();
        await PublishNativeChange(undrawn);
        var undrawnDesign = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        var unused = SchematicElectricalComparison.Compare(undrawnDesign, (await Capture()).Electrical, [], token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-sheets-undrawn-comparison.json"), JsonSerializer.Serialize(unused), token);
        Assert.IsTrue(unused.PinBindingsComplete); Assert.IsTrue(unused.ConnectivityEquivalent); Assert.AreEqual(2, unused.UndrawnPins!.Count);
        Assert.IsFalse(undrawnDesign.Engineering.Circuit.Symbols.Any(s => occurrences.Any(o => o.Id == s.Id && o.Unit == 2)));
        Assert.IsTrue(components.All(c => undrawnDesign.Engineering.Circuit.Components.Any(owner => owner.Id == c)));
        foreach (var instruction in removalInstructions)
            Assert.AreEqual(instruction, undrawnDesign.Engineering.Structure.Statements.Single(s => s.Id == instruction.Id));
        await NativeHistory("z");
        var unitUndo = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsTrue(occurrences.All(o => unitUndo.Engineering.Circuit.Symbols.Any(s => s.Id == o.Id)));
        Assert.IsTrue(unitUndo.Engineering.Circuit.Nets.Any(n => n.Id == connection));
        Assert.IsFalse(unitUndo.Engineering.Structure.HasUnresolvedNetBindings);
        await NativeHistory("y");
        Assert.IsFalse(SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []).Engineering.Circuit.Symbols
            .Any(s => occurrences.Any(o => o.Id == s.Id && o.Unit == 2)));

        var removeLast = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Remove final component drawings" };
        removeLast.Operations.Add(new SchematicItemOperation { Remove = new() { Value = nativeIds[0] } });
        removeLast.Operations.Add(new SchematicItemOperation { Remove = new() { Value = nativeIds[1] } });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(removeLast, token);
        await PublishNativeChange(await Capture());
        var retiredDesign = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsFalse(retiredDesign.Engineering.Circuit.Components.Any(c => components.Contains(c.Id)));
        Assert.IsTrue(retiredDesign.Engineering.HasUnresolvedComponentReferences);
        foreach (var instruction in removalInstructions)
        {
            Assert.AreEqual(instruction, retiredDesign.Engineering.Structure.Statements.Single(s => s.Id == instruction.Id));
            Assert.IsTrue(retiredDesign.Engineering.Structure.UnresolvedComponentReferences!.Any(r => r.OwnerId == instruction.Id));
        }
        Assert.IsTrue(SchematicElectricalComparison.Compare(retiredDesign, (await Capture()).Electrical, [], token).ConnectivityEquivalent);
        // A newer instruction-only synchronization has no displaced XML. Undo
        // must find the older verified identity receipt, not overwrite this text.
        var beforeNotes = store.Read()!;
        var withNotes = retiredDesign with { Engineering = retiredDesign.Engineering with { Structure = retiredDesign.Engineering.Structure with
        { Statements = retiredDesign.Engineering.Structure.Statements.Select(s => removalInstructions.Any(i => i.Id == s.Id)
            ? s with { Text = s.Text + " New instruction after deletion." } : s).ToArray() } } };
        byte[] noteXml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(withNotes, []));
        await File.WriteAllBytesAsync(designPath, noteXml, token);
        var noteInput = store.Save(beforeNotes.State with { DesiredFileBytes = noteXml }, beforeNotes.RevisionToken);
        var noteResult = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, noteInput.RevisionToken, Guid.NewGuid(), token);
        Assert.IsTrue(noteResult.SynchronizationCommitted); Assert.IsFalse(noteResult.NativeMutationCommitted);
        Assert.IsNull(store.Read()!.State.LastSynchronization!.PreviousXmlPath);
        await NativeHistory("z");
        var componentUndo = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsTrue(components.All(c => componentUndo.Engineering.Circuit.Components.Any(owner => owner.Id == c)));
        Assert.IsFalse(componentUndo.Engineering.HasUnresolvedComponentReferences);
        foreach (var instruction in removalInstructions)
            Assert.AreEqual(withNotes.Engineering.Structure.Statements.Single(s => s.Id == instruction.Id).Text,
                componentUndo.Engineering.Structure.Statements.Single(s => s.Id == instruction.Id).Text);
        await NativeHistory("z");
        var fullUndo = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsTrue(occurrences.All(o => fullUndo.Engineering.Circuit.Symbols.Any(s => s.Id == o.Id)));
        Assert.IsTrue(fullUndo.Engineering.Circuit.Nets.Any(n => n.Id == connection));
        Assert.IsFalse(fullUndo.Engineering.Structure.HasUnresolvedNetBindings);
        await NativeHistory("y"); await NativeHistory("y");
        var redo = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
        Assert.IsFalse(redo.Engineering.Circuit.Components.Any(c => components.Contains(c.Id)));
        Assert.IsTrue(redo.Engineering.HasUnresolvedComponentReferences);
        foreach (var instruction in removalInstructions)
            Assert.AreEqual(withNotes.Engineering.Structure.Statements.Single(s => s.Id == instruction.Id).Text,
                redo.Engineering.Structure.Statements.Single(s => s.Id == instruction.Id).Text);
        await VerifyRenamedSheetPublishesThroughItsSave(client, root, second, hierarchy.Directory, store, designPath, evidence, instanceId, token);
        // The separate service-restart fixture uses the standard title-block
        // command, whose existing contract requires the displayed sheet. The
        // offscreen-view preservation assertions above have already completed.
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
        await VerifySynchronizationServiceRestart(client, root, store, designPath, evidence, instanceId, token,
            ["publication-replaced", "baseline-committed", "retained-archived"]);
        await VerifyAutomaticSynchronization(client, root, store, designPath, evidence, instanceId, processId, display, token);
        Assert.AreEqual(3, restorationStops.Count);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-owner-restoration-restarts.json"), JsonSerializer.Serialize(restorationStops), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol-sheets-result.json"), JsonSerializer.Serialize(new
        { instanceId, physicalComponentsAcrossSheets = true, exactNativeBindings = true, referenceAndPlacementApplied = true,
            actualConnectivityVerified = true, visibleOtherInstancePreserved = true, operationReplayedOnce = true,
            knownUndrawnPinsReported = true, nativeUnitRemovalReversePublication = true, nativeComponentRemovalReversePublication = true,
            removedComponentInstructionsRetained = true, retainedXmlContentVerified = true,
            retainedXmlServiceRecoveryVerified = true,
            nativeKeyboardOwnerUndoRedo = true, newerInstructionsPreservedThroughUndoRedo = true,
            explicitOwnershipChoiceUsed = true, restorationServiceRecoveryVerified = true,
            apiGlobalLabelReferenceFieldsSurviveSchematicSetup = true,
            automaticOwnershipReconciliationQualified = false, crossPlatformReady = false }), token);

        async Task PublishNativeChange(CheckedSchematicState observed)
        {
            var prior = store.Read()!;
            byte[] expectedPrevious = prior.State.DesiredFileBytes.ToArray();
            var intake = store.Save(prior.State with { Observed = observed.Electrical.Hierarchy.Data.Clone(),
                ObservedElectrical = observed.Electrical.Clone(), NativeRevision = new(observed.State.Revision.Epoch, observed.State.Revision.Sequence),
                TrackingComplete = observed.Electrical.Hierarchy.TrackingComplete }, prior.RevisionToken);
            var plan = await planningHost.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = intake.RevisionToken });
            RequireToolSuccess(plan);
            bool restoring = plan.GetProperty("structuredContent").GetProperty("restoredSymbolOccurrences") is { ValueKind: JsonValueKind.Array } restored
                && restored.GetArrayLength() > 0;
            if (restoring)
            {
                var inspection = await planningHost.Tool("kicad_design_owner_history_inspect", new { instanceId,
                    recoveryPath = store.StatePath, expectedRevisionToken = intake.RevisionToken });
                RequireToolSuccess(inspection);
                var options = inspection.GetProperty("structuredContent");
                var selected = await planningHost.Tool("kicad_design_owner_history_resolve", new { instanceId,
                    recoveryPath = store.StatePath, expectedRevisionToken = intake.RevisionToken,
                    expectedSnapshotToken = options.GetProperty("snapshotToken").GetString(),
                    historyOperationId = options.GetProperty("choices")[0].GetProperty("historyOperationId").GetString() });
                RequireToolSuccess(selected); intake = store.Read()!;
                Assert.IsNotNull(intake.State.OwnershipResolution);
            }
            Guid sync = Guid.NewGuid();
            if (restoring)
                await RestoreAcrossServiceStop(intake, sync, new[] { "publication-replaced", "baseline-committed", "retained-archived" }[restorationStops.Count]);
            var applied = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, intake.RevisionToken, sync, token);
            Assert.IsTrue(applied.SynchronizationCommitted); Assert.IsFalse(applied.NativeMutationCommitted);
            Assert.IsNull(store.Read()!.State.OwnershipResolution);
            var receipt = store.Read()!.State.LastSynchronization!;
            Assert.AreEqual(2, receipt.Version);
            string expectedDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expectedPrevious));
            Assert.AreEqual(expectedDigest, receipt.PreviousXmlSha256);
            Assert.IsTrue(RetainedXmlHistory.Inspect(receipt).ContentVerified);
            CollectionAssert.AreEqual(expectedPrevious, await RetainedXmlHistory.ReadVerifiedAsync(receipt, token));
            var current = await Capture();
            var design = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(current.Electrical.Hierarchy.Data, design.Schematic, token));
            Assert.IsTrue((await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, intake.RevisionToken, sync, token)).Replayed);
            Assert.AreEqual(current, await Capture());
        }

        async Task RestoreAcrossServiceStop(StoredDesignRecovery intake, Guid operationId, string stage)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(45));
            string name = instanceId + "-owner-restore-" + stage;
            string marker = Path.Combine(evidence, name + "-pause.json");
            string hostState = Path.Combine(evidence, instanceId + "-owner-restore-host");
            var args = new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = intake.RevisionToken, operationId = operationId.ToString("D") };
            CheckedSchematicState beforeStop;
            await using (var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(stage, marker), hostState,
                Path.Combine(evidence, name + "-first.log"), limit.Token))
            {
                RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                Task ready = SyncHarnessProcessTests.WaitForMarkerAsync(marker, limit.Token);
                var call = host.Tool("kicad_design_sync_apply", args);
                try
                {
                    if (await Task.WhenAny(ready, call) == call)
                    { RequireToolSuccess(await call); Assert.Fail("Restoration completed without the requested interruption checkpoint."); }
                    await ready; beforeStop = await Capture();
                    var pending = store.Read()!;
                    if (stage == "publication-replaced") Assert.IsNotNull(pending.State.OwnershipResolution);
                    else Assert.IsNull(pending.State.OwnershipResolution);
                    await host.TerminateAsync(); Assert.IsTrue(host.ForcedTermination);
                    await Assert.ThrowsAsync<IOException>(async () => { await call; });
                }
                catch { await host.TerminateAsync(); try { await call; } catch (Exception) { } throw; }
            }
            Assert.AreEqual(beforeStop, await Capture(), "Stopping the service must not modify the native restored objects.");
            await using (var restarted = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(), hostState,
                Path.Combine(evidence, name + "-restarted.log"), limit.Token))
            {
                RequireToolSuccess(await restarted.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                var result = await restarted.Tool("kicad_design_sync_apply", args); RequireToolSuccess(result);
                var data = result.GetProperty("structuredContent");
                Assert.IsTrue(data.GetProperty("synchronizationCommitted").GetBoolean());
                Assert.IsFalse(data.GetProperty("nativeMutationCommitted").GetBoolean());
                Assert.AreEqual(stage != "publication-replaced", data.GetProperty("replayed").GetBoolean());
            }
            var after = await Capture(); Assert.AreEqual(beforeStop.State.Revision, after.State.Revision);
            Assert.AreEqual(beforeStop.State.ProcessEpoch, after.State.ProcessEpoch);
            Assert.IsFalse(store.Read()!.State.HasPendingWork); Assert.IsNull(store.Read()!.State.OwnershipResolution);
            restorationStops.Add(new { stage, operationId, actualServiceProcessTerminated = true,
                nativeEpochAndRevisionPreserved = true, selectedMappingConsumedOnce = true });
        }

        async Task NativeHistory(string key)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, limit.Token);
            var beforeHistory = await Capture();
            await FocusedSchematicShortcut(client, root, processId, display, key, limit.Token);
            CheckedSchematicState current;
            do
            {
                current = await Capture();
                if (current.State.Revision.Equals(beforeHistory.State.Revision)) await Task.Delay(40, limit.Token);
            } while (current.State.Revision.Equals(beforeHistory.State.Revision));
            await PublishNativeChange(current);
        }

        // Every label written above keeps KiCad's hidden inter-sheet reference field, and the
        // Schematic Setup refresh that reads it neither stops KiCad nor touches another field.
        async Task VerifyApiGlobalLabelFields()
        {
            GlobalLabel NativeLabel(CheckedSchematicState state, DocumentSpecifier sheet, GlobalLabel sent) =>
                state.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(sheet)).Items
                    .Where(i => i.Is(GlobalLabel.Descriptor)).Select(i => i.Unpack<GlobalLabel>()).Single(l => l.Id.Equals(sent.Id));
            // Custom fields in stored order, with the text and visibility the request gave them.
            static string[] Custom(GlobalLabel label) => [.. label.Fields.Select(f => $"{f.Name}|{f.Text.Text_}|{f.Visible}")];
            // KiCad refreshes the reference fields of the sheet it shows; the others keep theirs.
            string RequireFields(CheckedSchematicState state, DocumentSpecifier? shownOn, string phase)
            {
                foreach (var (sheet, sent) in labels)
                {
                    var label = NativeLabel(state, sheet, sent);
                    // The field is identified by its own message; KiCad reports its display name.
                    Assert.IsNotNull(label.IntersheetRefsField, $"{phase}: every global label keeps its inter-sheet reference field.");
                    Assert.AreEqual("${INTERSHEET_REFS}", label.IntersheetRefsField.Text.Text_, phase);
                    Assert.AreEqual(shownOn is not null && sheet.Equals(shownOn), label.IntersheetRefsField.Visible,
                        $"{phase}: only the reference fields of the refreshed sheet follow the project setting.");
                    CollectionAssert.AreEqual(Custom(sent), Custom(label), $"{phase}: custom fields keep their order, text and visibility.");
                }
                return JsonSerializer.Serialize(labels.Select(l => NativeLabel(state, l.Sheet, l.Sent)).Select(l => new
                {
                    id = l.Id.Value, referenceVisible = l.IntersheetRefsField.Visible, referenceText = l.IntersheetRefsField.Text.Text_,
                    referenceX = l.IntersheetRefsField.Text.Position.XNm, referenceY = l.IntersheetRefsField.Text.Position.YNm,
                    custom = Custom(l)
                }));
            }
            var phases = new Dictionary<string, string>();

            var labelsCreated = await Capture();
            phases["created"] = RequireFields(labelsCreated, null, "Created");
            foreach (var (sheet, sent) in labels)
            {
                var label = NativeLabel(labelsCreated, sheet, sent);
                // Unset in the request: the one KiCad gives a new label, on the label.
                Assert.AreEqual(sent.IntersheetRefsField?.Text.Position ?? label.Position, label.IntersheetRefsField.Text.Position,
                    "A requested reference field is kept; a missing one is created on the label.");
            }

            // A custom field under the reference field's name would replace that field when the
            // saved file is read back, so KiCad refuses the whole batch and changes nothing.
            var shadowing = Label(new() { XNm = 150000000, YNm = 180000000 }, custom: true);
            shadowing.Fields[1].Name = "intersheetrefs";
            var refusedBatch = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Shadowed reference field" };
            refusedBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(shadowing) });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(refusedBatch, token));
            Assert.AreEqual(labelsCreated, await Capture(), "A refused label must leave the design exactly as it was.");

            // Opening Schematic Setup and pressing OK refreshes the reference fields of the shown
            // sheet. Unchanged, it must record nothing and leave every label as it was.
            await AcceptUnchangedSetup(root, "hidden-root");
            await AcceptUnchangedSetup(first, "hidden-child");
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);

            // Shown references show exactly the reference fields, whatever custom fields the
            // label carries: the refresh finds the field itself.
            var screenBefore = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = root.Clone() }, token);
            var originalFormatting = screenBefore.Data.Metadata.Formatting.Clone();
            Assert.IsFalse(originalFormatting.ShowIntersheetReferences, "The fixture starts with inter-sheet references hidden.");
            async Task ShowReferences(bool referencesShown)
            {
                var screen = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                    new() { Document = root.Clone() }, token);
                var desiredFormatting = originalFormatting.Clone(); desiredFormatting.ShowIntersheetReferences = referencesShown;
                var formattingBatch = new ApplySchematicItemBatch { Document = root.Clone(), ExpectedRevision = screen.Revision.Clone(),
                    DocumentEpoch = screen.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"),
                    Description = referencesShown ? "Show inter-sheet references" : "Hide inter-sheet references" };
                formattingBatch.Operations.Add(new SchematicItemOperation { SetFormatting = desiredFormatting });
                Assert.IsTrue((await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(formattingBatch, token))
                    .FormattingChanged);
            }
            await ShowReferences(true);
            phases["shown"] = RequireFields(await Capture(), root, "Shown");
            var shownView = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root.Clone() }, token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-global-label-references-shown.png"),
                shownView.Preview.Png.ToByteArray(), token);
            await AcceptUnchangedSetup(root, "shown-root");
            await ShowReferences(false);
            phases["hidden"] = RequireFields(await Capture(), null, "Hidden again");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-global-label-fields.json"),
                JsonSerializer.Serialize(phases), token);
        }

        async Task AcceptUnchangedSetup(DocumentSpecifier sheet, string stage)
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = sheet.Clone() }, token);
            var beforeSetup = await Capture();
            // Its readiness probe reads the page settings of the displayed sheet.
            await NativeSetupUi.Open(client, sheet, display, processId, token);
            // Every accepted Setup journey returns to the Formatting row before OK.
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false, clickFromRight: 60, clickFromBottom: 25);
            using (var closing = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                closing.CancelAfter(TimeSpan.FromSeconds(15));
                int delay = 25;
                try
                {
                    while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                    { await Task.Delay(delay, closing.Token); delay = Math.Min(delay * 2, 500); }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // A Debug build stops on a failed native assertion in a modal window.
                    var windows = new List<string>();
                    NativeKeyboard.HasWindow(display, processId, "Schematic Setup", describe: windows.Add);
                    await File.WriteAllLinesAsync(Path.Combine(evidence, instanceId + "-global-label-setup-" + stage + "-windows.txt"), windows, token);
                    await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-global-label-setup-" + stage + ".png"), token);
                    Assert.Fail($"Schematic Setup ({stage}) did not close after OK; see the retained window list and screenshot.");
                }
            }
            // OK also saves the project file, so only the files' baselines may differ.
            var afterSetup = await Capture();
            Assert.AreEqual(beforeSetup.State.Revision, afterSetup.State.Revision,
                $"Accepting an unchanged Schematic Setup ({stage}) must record nothing.");
            Assert.AreEqual(beforeSetup.State.StateSha256, afterSetup.State.StateSha256, stage);
            Assert.AreEqual(beforeSetup.Electrical, afterSetup.Electrical,
                $"Accepting an unchanged Schematic Setup ({stage}) must leave every global label as it was.");
        }

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
    }
}
