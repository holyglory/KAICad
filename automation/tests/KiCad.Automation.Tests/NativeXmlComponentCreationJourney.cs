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
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9) for CN-1 milestone 1 end to end (ledgers
    // pf1134ca32f913781 and p186c0db05be2a146), on the S1 seed, through the public MCP tools over STDIO against this editor,
    // which advertises schematic.connection-realization.v1:
    // 1. The connection-aware layout tool places the fixture's Complete stage (CN-1 §10), and its Components stage (the same
    //    placements, no nets) is created first.
    // 2. The Complete stage's XML then adds all 11 nets across ROOT, PSU, CPU and CPU_POWER, including the stacked U2.1/U2.4 and
    //    processor unit 4 on CPU_POWER. The preview is the realization plan; realizing it from this editor's measurements is
    //    recorded as a replay fixture.
    // 3. A forced mismatch first: that realization's batch, journaled with one foreign wire joining two of its nets, is refused
    //    by KiCad with connectivity_postcondition_failed, apply reports realization_connectivity_mismatch, and nothing is
    //    committed: KiCad's state, change journal and drawing, the XML and the recovery baseline are all unchanged.
    // 4. Apply then draws exactly that realization in one checked KiCad commit whose connectivity assertion KiCad verified, and
    //    publishes the XML. KiCad's pin partition is exactly the 11 nets (PsuCpuFixture.AssertNative), and every generated stub,
    //    label and sheet pin follows CN-1 §6. Replaying the apply returns its receipt, a repeated plan has nothing to do and a
    //    repeated apply is a no-op.
    // 5. One native undo removes the whole realization and redo restores it; save and reload keep it, and the recovery record
    //    adopts the reloaded editor with nothing left to do.
    // 6. I2C pull-ups added to connected nets (two coordinate-free, one placed and locked) are placed by the connection-aware
    //    layout tool, then created and connected by one more realization.
    private static async Task VerifyPsuCpuConnectedRealization(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Complete);
        var document = context.Root;
        var seedBaseline = context.Baseline ?? throw new AssertFailedException("The S1 seed must provide a native baseline.");
        string path = context.DesignPath;
        string Evidence(string name) => Path.Combine(evidence, instanceId + "-psu-cpu-connected-" + name);
        var store = new DesignRecoveryStore(Evidence("recovery.json"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<object>();
        var problems = new List<string>();
        void Step(string name) { steps.Add(new { name, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
            Console.WriteLine($"PSU/CPU connected realization {instanceId}: {name} at {clock.Elapsed.TotalSeconds:F1}s"); }
        // The same project-file workaround as VerifyPsuCpuComponentCreation: the harness wrote the S1 files directly, so KiCad
        // saves the project once before any XML is applied.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        var saved = await PsuCpuFixture.InitializeRecoveryAsync(client, context, store.StatePath, token);
        var session = RequireRealizationAdvertised(await client.HandshakeAsync(token));
        Assert.AreEqual(instanceId, session.InstanceId);
        var seeded = await Capture();
        Assert.IsFalse(seeded.State.NativeContentDirty);
        var (regions, usable, sheetPaths) = await PsuCpuRegions(client, seedBaseline, expected, seeded.State.Revision, token);
        // The production MCP server: its preview classifies with the handshake recorded when it attached this editor.
        await using var host = await StdioMcpFixture.StartAsync(Evidence("host"), Evidence("host.log"), token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        // 1. The connection-aware layout of the whole Complete stage, then the Components stage created with its placements.
        var completeStage = PsuCpuFixture.Desired(context, PsuCpuStage.Complete);
        Assert.HasCount(11, completeStage.Engineering.Circuit.Nets);
        Assert.IsTrue(completeStage.Engineering.Circuit.Symbols.All(s => s.Placement is null), "The frozen fixture occurrences are coordinate-free.");
        saved = Write(saved, completeStage);
        var layout = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = saved.RevisionToken, gridNm = PsuCpuLayoutPolicy.GridNm, clearanceNm = PsuCpuLayoutPolicy.ClearanceNm,
            pageInsetNm = PsuCpuLayoutPolicy.PageInsetNm, regions,
            userInstructions = "Lay out the PSU/CPU circuit on its sheets with room for every connection." });
        await File.WriteAllTextAsync(Evidence("layout.json"), RetainedToolEvidence(layout), token);
        RequireToolSuccess(layout);
        Assert.IsTrue(layout.GetProperty("structuredContent").GetProperty("canPropose").GetBoolean(), layout.GetRawText());
        var laidOut = SchematicDesignXml.Read(layout.GetProperty("structuredContent").GetProperty("desiredXml").GetString()!, []);
        Assert.AreEqual(CircuitXml.Write(completeStage.Engineering.Circuit), CircuitXml.Write(laidOut.Engineering.Circuit.WithoutPlacement()),
            "Layout only adds coordinates.");
        var placements = laidOut.Engineering.Circuit.Symbols.ToDictionary(s => s.Id, s => s.Placement ?? throw new AssertFailedException("Every unit is placed."));
        Assert.AreEqual(seeded, await Capture(), "Layout must not change KiCad.");
        Step("complete-stage layout");

        var componentsStage = PsuCpuFixture.Desired(context, PsuCpuStage.Components);
        componentsStage = componentsStage with { Engineering = componentsStage.Engineering with { Circuit = componentsStage.Engineering.Circuit with
            { Symbols = [.. componentsStage.Engineering.Circuit.Symbols.Select(s => s with { Placement = placements[s.Id] })] } } };
        Assert.IsEmpty(componentsStage.Engineering.Circuit.Nets);
        saved = Write(store.Read()!, componentsStage);
        var creationPreview = await Preview("components");
        Assert.IsNotNull(creationPreview.GetProperty("candidateDesignXml").GetString(), "Creation previews its candidate XML.");
        Assert.AreEqual(expected.Symbols.Count, creationPreview.GetProperty("nativeOperationsJson").EnumerateArray()
            .Select(o => SchematicJson.Parser.Parse<SchematicItemOperation>(o.GetString()!))
            .Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true), "Eleven units are created.");
        var creation = await Apply("components");
        Assert.IsTrue(creation.GetProperty("nativeMutationCommitted").GetBoolean(), creation.GetRawText());
        var components = await Capture();
        var created = store.Read()!;
        PsuCpuFixture.AssertNative(created.State.Baseline, components.Electrical, PsuCpuStage.Components);
        Assert.IsFalse(components.State.NativeContentDirty, "Apply saves the created sheets.");
        Step("components created");

        // 2. The Complete stage: the created design with the fixture's 11 nets. Its preview is the realization plan.
        var nets = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets;
        var completeDesign = created.State.Baseline with { Engineering = created.State.Baseline.Engineering with
            { Circuit = created.State.Baseline.Engineering.Circuit with { Nets = nets } } };
        saved = Write(created, completeDesign);
        byte[] completeXml = await File.ReadAllBytesAsync(path, token);
        var realizationPreview = await Preview("complete");
        Assert.AreEqual(JsonValueKind.Null, realizationPreview.GetProperty("candidateDesignXml").ValueKind,
            "A realization plan has no publishable preview (CN-1 §4.2).");
        Assert.AreEqual(0, realizationPreview.GetProperty("nativeOperationsJson").GetArrayLength(), "Only the realization produces native operations.");
        Assert.IsTrue(realizationPreview.GetProperty("nativeConnectivityValidationRequired").GetBoolean());
        var plan = SchematicSynchronizationPlanner.Plan(saved.State, session, token);
        var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
        SchematicSynchronizationPlanTests.RequirePsuCpuIntent(intent, plan.Candidate!, created: false);
        Assert.IsEmpty(intent.CreatedSymbolIds, "The Complete stage only connects the created units.");
        Assert.AreEqual(components, await Capture(), "Planning must not change KiCad.");

        // The realization of this exact checkpoint from this editor's measurements, recorded as a replay fixture. The executor
        // plans and measures the same checkpoint, so its batch must be exactly this one (CN-1 I9).
        var checkpoint = await Capture();
        var recorded = new List<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)>();
        async Task<SchematicPlacementGeometry> Recording(MeasureSchematicPlacement request, CancellationToken cancellation)
        {
            var reply = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, cancellation);
            recorded.Add((request.Clone(), reply.Clone()));
            return reply;
        }
        var policy = SchematicConnectionPolicy.FromSnapshot(checkpoint.Electrical.Hierarchy.Data);
        SchematicConnectionRealization realization;
        try { realization = await SchematicConnectionRealizer.RealizeAsync(intent, plan.Candidate!, checkpoint, Recording, policy, token); }
        catch (AutomationException refusal)
        {
            await KeepRecording("complete", created.State, saved.State, checkpoint, recorded, null, refusal);
            throw;
        }
        await KeepRecording("complete", created.State, saved.State, checkpoint, recorded, realization, null);
        var prepared = await SchematicConnectedAddition.RealizeAsync(SchematicConnectionRealizerTests.Replay(recorded), session, saved.State, plan,
            checkpoint, token);
        CollectionAssert.AreEqual(realization.Operations.Select(o => o.ToByteString()).ToArray(), prepared.Operations.Select(o => o.ToByteString()).ToArray(),
            "The same measurements give the same batch (I9).");
        Assert.AreEqual(checkpoint, await Capture(), "Measuring must not change KiCad.");
        Step("realization measured");

        // 3. Forced mismatch through the executor: the same batch journaled with a foreign wire from the VIN label to the DCDC_OUT
        // label on PSU. KiCad's assertion refuses it and the executor abandons it (CN-1 §8.1, §9.2, §9.3).
        var mismatch = await RequireForcedMismatch(checkpoint, completeXml);
        Step("forced mismatch refused");

        // 4. Apply realizes the Complete stage.
        Guid operation = Guid.NewGuid();
        var applyArguments = new { instanceId, recoveryPath = store.StatePath, designPath = path, expectedRevisionToken = store.Read()!.RevisionToken,
            operationId = operation.ToString("D") };
        var applied = await host.Tool("kicad_design_sync_apply", applyArguments);
        await File.WriteAllTextAsync(Evidence("complete-apply.json"), RetainedToolEvidence(applied), token);
        RequireToolSuccess(applied);
        var result = applied.GetProperty("structuredContent");
        Assert.IsTrue(result.GetProperty("nativeMutationCommitted").GetBoolean(), applied.GetRawText());
        Assert.IsTrue(result.GetProperty("nativeFilesSaved").GetBoolean(), applied.GetRawText());
        Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), applied.GetRawText());
        Assert.IsTrue(result.GetProperty("nativeReceipt").GetProperty("result").GetProperty("connectivityAssertionVerified").GetBoolean(),
            "KiCad verified the connectivity assertion of the realization: " + applied.GetRawText());
        var realized = await Capture();
        Assert.IsFalse(realized.State.NativeContentDirty, "Apply saves the realized sheets.");
        var synchronized = store.Read()!.State.Baseline;
        PsuCpuFixture.AssertNative(synchronized, realized.Electrical, PsuCpuStage.Complete);
        string publishedXml = await File.ReadAllTextAsync(path, token);
        Assert.AreEqual(SchematicDesignXml.Write(synchronized, []), SchematicDesignXml.Write(SchematicDesignXml.Read(publishedXml, []) with
            { Schematic = synchronized.Schematic }, []), "The published XML is the synchronized design.");
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(realized.Electrical.Hierarchy.Data, SchematicDesignXml.Read(publishedXml, []).Schematic, token),
            "The XML holds exactly KiCad's drawing.");
        // The executor drew exactly the recorded realization: the same generated items and sheet pins, with the same identities.
        var drawn = GeneratedIn(components, realized);
        CollectionAssert.AreEquivalent(realization.Generated.Select(g => g.Id).ToArray(), drawn.ToArray(),
            "KiCad holds exactly the generated items of the recorded realization.");
        var stubs = await RequireLabelStubs(client, components, realized, policy, token);
        await CheckPresentation(realized, synchronized, "complete");
        Step("complete stage realized");

        var replay = await host.Tool("kicad_design_sync_apply", applyArguments);
        RequireToolSuccess(replay);
        Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(), replay.GetRawText());
        var repeated = await Preview("repeated");
        var repeatedDesign = SchematicDesignXml.Read(repeated.GetProperty("candidateDesignXml").GetString()
            ?? throw new AssertFailedException("A repeated plan is the general path, with its candidate XML."), []);
        var publishedNow = SchematicDesignXml.Read(publishedXml, []);
        Assert.AreEqual(publishedXml, SchematicDesignXml.Write(repeatedDesign with { Schematic = publishedNow.Schematic }, []),
            "A repeated plan publishes nothing new.");
        Assert.IsTrue(Same(repeatedDesign.Schematic, publishedNow.Schematic), "A repeated plan draws nothing new.");
        Assert.AreEqual(0, repeated.GetProperty("nativeOperationsJson").GetArrayLength(), "A repeated plan sends nothing to KiCad.");
        var again = await Apply("repeated");
        Assert.IsFalse(again.GetProperty("nativeMutationCommitted").GetBoolean(), again.GetRawText());
        Assert.IsFalse(again.GetProperty("nativeFilesSaved").GetBoolean(), again.GetRawText());
        Assert.AreEqual(realized, await Capture(), "A repeated apply leaves KiCad unchanged.");
        Assert.AreEqual(publishedXml, await File.ReadAllTextAsync(path, token), "A repeated apply leaves the XML unchanged.");
        Step("repeat is a no-op");

        // 5. One native undo removes the whole realization, and redo restores it.
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        var undone = await Until("native undo", s => Same(s.Electrical.Hierarchy.Data, components.Electrical.Hierarchy.Data));
        Assert.IsEmpty(GeneratedIn(components, undone), "Undo removes every generated wire, label and sheet pin.");
        PsuCpuFixture.AssertNative(created.State.Baseline, undone.Electrical, PsuCpuStage.Components);
        await FocusedSchematicShortcut(client, document, processId, display, "y", token);
        var redone = await Until("native redo", s => Same(s.Electrical.Hierarchy.Data, realized.Electrical.Hierarchy.Data));
        PsuCpuFixture.AssertNative(synchronized, redone.Electrical, PsuCpuStage.Complete);
        Step("undo and redo");

        // Save and reload the realized sheets from disk.
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document.Clone() }, token);
        Assert.IsFalse((await Capture()).State.NativeContentDirty);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        var reloaded = await Capture();
        Assert.IsFalse(reloaded.State.NativeContentDirty);
        Assert.AreNotEqual(realized.State.Revision.Epoch, reloaded.State.Revision.Epoch, "Revert reloads a new native document.");
        // Loading the files KiCad wrote changes each sheet's loaded-format provenance, not any persisted object.
        var loadedExpected = realized.Electrical.Hierarchy.Data.Clone();
        foreach (var screen in loadedExpected.Instances)
        {
            var actual = reloaded.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document));
            Assert.AreEqual(screen.Metadata.WriterNativeFormatVersion, actual.Metadata.LoadedNativeFormatVersion);
            screen.Metadata.LoadedNativeFormatVersion = actual.Metadata.LoadedNativeFormatVersion;
        }
        // A difference is recorded with every changed field and the journey continues on the reloaded editor, so one run
        // reports every later problem too; the journey still fails at its end.
        var reloadDifferences = Differences(reloaded.Electrical.Hierarchy.Data, loadedExpected);
        if (reloadDifferences.Count != 0)
        {
            await File.WriteAllTextAsync(Evidence("realized.xml"), SchematicDataXml.Write(realized.Electrical.Hierarchy.Data), token);
            await File.WriteAllTextAsync(Evidence("reloaded.xml"), SchematicDataXml.Write(reloaded.Electrical.Hierarchy.Data), token);
            problems.Add("Save and reload changed the realized sheets: " + string.Join("; ", reloadDifferences.Take(12)));
        }
        PsuCpuFixture.AssertNative(synchronized, reloaded.Electrical, PsuCpuStage.Complete);
        var beforeReattach = store.Read()!;
        RequireToolSuccess(await host.Tool("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = beforeReattach.RevisionToken, expectedDocumentEpoch = reloaded.State.Revision.Epoch }));
        Assert.AreEqual(SchematicDesignXml.Write(beforeReattach.State.Baseline, []), SchematicDesignXml.Write(store.Read()!.State.Baseline, []),
            "The recovery record adopts the reloaded editor unchanged.");
        var settled = await Preview("reloaded");
        Assert.AreEqual(0, settled.GetProperty("nativeOperationsJson").GetArrayLength(), "Nothing is left to send to KiCad.");
        Assert.AreEqual(0, settled.GetProperty("netChanges").GetArrayLength(), "No published net changes.");
        Assert.AreEqual(0, settled.GetProperty("electricalConflicts").GetArrayLength());
        // The settled candidate is the published XML apart from the reloaded files' loaded-format provenance, which a reload
        // records and which is published to XML, never sent to KiCad.
        var publishedDesign = SchematicDesignXml.Read(publishedXml, []);
        var settledDesign = SchematicDesignXml.Read(settled.GetProperty("candidateDesignXml").GetString()!, []);
        var expectedSettled = publishedDesign with { Schematic = publishedDesign.Schematic.Clone() };
        foreach (var screen in expectedSettled.Schematic.Instances)
            screen.Metadata.LoadedNativeFormatVersion = reloaded.Electrical.Hierarchy.Data.Instances
                .Single(s => s.Metadata.Document.Equals(screen.Metadata.Document)).Metadata.LoadedNativeFormatVersion;
        bool settledIsPublished = SchematicDesignXml.Write(expectedSettled, []) == SchematicDesignXml.Write(settledDesign, []);
        if (!settledIsPublished)
        {
            await File.WriteAllTextAsync(Evidence("settled-expected.xml"), SchematicDesignXml.Write(expectedSettled, []), token);
            await File.WriteAllTextAsync(Evidence("settled-actual.xml"), SchematicDesignXml.Write(settledDesign, []), token);
            problems.Add("Apart from the reloaded files' provenance, the settled candidate must be exactly the published XML: "
                + string.Join("; ", Differences(settledDesign.Schematic, expectedSettled.Schematic).Take(8))
                + (SchematicDesignXml.Write(expectedSettled with { Schematic = settledDesign.Schematic }, []) == SchematicDesignXml.Write(settledDesign, [])
                    ? "" : " (the engineering design differs too)"));
        }
        Assert.AreEqual(reloaded, await Capture());
        // Applying the settled plan publishes that provenance to the XML and sends nothing to KiCad; the next XML change
        // then starts from the reloaded editor.
        var provenance = await Apply("reloaded");
        Assert.IsFalse(provenance.GetProperty("nativeMutationCommitted").GetBoolean(), provenance.GetRawText());
        Assert.AreEqual(settled.GetProperty("candidateDesignXml").GetString(), await File.ReadAllTextAsync(path, token));
        Assert.IsTrue(Same(reloaded.Electrical.Hierarchy.Data, (await Capture()).Electrical.Hierarchy.Data), "Publishing provenance changes nothing in KiCad.");
        PsuCpuFixture.AssertNative(store.Read()!.State.Baseline, (await Capture()).Electrical, PsuCpuStage.Complete);
        Step("save, reload and reattach");

        // 6. Pull-ups on the connected I2C nets, placed by the connection-aware layout tool and realized.
        var pullUps = await RequirePullUpRealization(await Capture());
        Step("pull-ups realized");

        await NativeKeyboard.CaptureAsync(display, Evidence("window.png"), token);
        byte[] published = await File.ReadAllBytesAsync(path, token);
        await File.WriteAllTextAsync(Evidence("proof.json"), JsonSerializer.Serialize(new
        {
            instanceId, fixture = "psu-cpu", PsuCpuFixture.Version, stage = PsuCpuStage.Complete.ToString(), seed = context.Seed.ToString(),
            nativeCapabilities = session.Capabilities.ToArray(), realStdioApply = true, operation,
            intent = new { nets = intent.Nets.Count, islands = intent.Screens.Sum(s => s.Islands.Count), ports = intent.Ports.Count,
                expectedGroups = intent.ExpectedGroups.Count },
            generated = realization.Generated.GroupBy(g => g.Role.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            connectivityAssertionVerified = true, executorDrewRecordedRealization = true, labelStubs = stubs, forcedMismatch = mismatch,
            exactReplay = true, repeatedPlanNoOp = true, repeatedApplyNoOp = true, nativeUndoRedoVerified = true,
            // Save and reload hold only when the reloaded sheets equal the realized ones and the settled plan is the published XML;
            // either difference is also recorded in problems, and the journey fails below.
            saveReloadVerified = reloadDifferences.Count == 0 && settledIsPublished,
            recoveryReattachedWithoutChanges = true, pullUps,
            publishedXml = new { length = published.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(published)) },
            crossPlatformReady = false, steps, problems
        }), token);
        Step("done");
        if (problems.Count != 0) throw new AssertFailedException(string.Join(" | ", problems));

        Task<CheckedSchematicState> Capture() => CaptureChecked(client, document, token);

        // Write a design as the saved XML and the record's desired revision.
        StoredDesignRecovery Write(StoredDesignRecovery current, SchematicDesign design)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));
            File.WriteAllBytes(path, bytes);
            return store.Save(current.State with { DesiredFileBytes = bytes }, current.RevisionToken);
        }

        // The public preview of the current record; it never changes KiCad or the record.
        async Task<JsonElement> Preview(string name)
        {
            var current = store.Read()!;
            var nativeBefore = await Capture();
            var preview = await host.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = current.RevisionToken });
            await File.WriteAllTextAsync(Evidence(name + "-plan.json"), RetainedToolEvidence(preview), token);
            RequireToolSuccess(preview);
            var content = preview.GetProperty("structuredContent").Clone();
            Assert.IsTrue(content.GetProperty("canPrepare").GetBoolean(), preview.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, content.GetProperty("errorCode").ValueKind, preview.GetRawText());
            Assert.AreEqual(nativeBefore, await Capture(), name + ": planning must not change KiCad.");
            Assert.AreEqual(current.RevisionToken, store.Read()!.RevisionToken, name + ": planning writes nothing.");
            return content;
        }

        // Apply the current record through the public tool and require it to succeed.
        async Task<JsonElement> Apply(string name)
        {
            var current = store.Read()!;
            var reply = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath = path,
                expectedRevisionToken = current.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            await File.WriteAllTextAsync(Evidence(name + "-apply.json"), RetainedToolEvidence(reply), token);
            RequireToolSuccess(reply);
            return reply.GetProperty("structuredContent").Clone();
        }

        // The recorded measurements beside the saved record they were planned from, in the replay-fixture format of
        // automation/tests/fixtures/connection-realization (the record compressed, because the evidence has a size cap).
        async Task KeepRecording(string name, DesignRecoveryState record, DesignRecoveryState revision, CheckedSchematicState at,
            IReadOnlyList<(MeasureSchematicPlacement Request, SchematicPlacementGeometry Reply)> measurements,
            SchematicConnectionRealization? outcome, Exception? failure)
        {
            string kept = Evidence("realization-" + name + ".recovery.json");
            new DesignRecoveryStore(kept).Save(record with { LastSynchronization = null }, null);
            await using (var input = File.OpenRead(kept))
            await using (var output = File.Create(kept + ".gz"))
            await using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
                await input.CopyToAsync(zip, CancellationToken.None);
            File.Delete(kept); File.Delete(kept + ".lock");
            string text = SchematicConnectionRealizerTests.FormatRecording("psu-cpu-" + name, revision, at, measurements, outcome,
                Path.GetFileName(kept), failure);
            await using (var output = File.Create(Evidence("realization-" + name + (failure is null ? ".measurement.json.gz" : ".refused.json.gz"))))
            await using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
                await zip.WriteAsync(Encoding.UTF8.GetBytes(text), CancellationToken.None);
        }

        // KiCad's identities of the generated items: new top-level items and new sheet pins of existing sheet symbols.
        static IReadOnlySet<Guid> GeneratedIn(CheckedSchematicState before, CheckedSchematicState after)
        {
            var result = new HashSet<Guid>();
            foreach (var screen in after.Electrical.Hierarchy.Data.Instances)
            {
                var old = before.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document));
                var oldItems = SchematicItemDelta.Index(old.Items);
                var oldPins = oldItems.Values.OfType<SheetSymbol>().SelectMany(s => s.Pins).Select(p => Guid.Parse(p.Id.Value)).ToHashSet();
                foreach (var (id, item) in SchematicItemDelta.Index(screen.Items))
                {
                    if (!oldItems.ContainsKey(id)) result.Add(id);
                    if (item is SheetSymbol sheet)
                        foreach (var pin in sheet.Pins.Select(p => Guid.Parse(p.Id.Value)).Where(p => !oldPins.Contains(p))) result.Add(pin);
                }
            }
            return result;
        }

        bool Same(SchematicHierarchyData left, SchematicHierarchyData right) =>
            SchematicHierarchyDelta.Plan(left, right, token).Count == 0 && SchematicHierarchyDelta.Plan(right, left, token).Count == 0;

        // What turns `actual` into `wanted`: each operation with, for an update, every top-level field that differs.
        List<string> Differences(SchematicHierarchyData actual, SchematicHierarchyData wanted)
        {
            IReadOnlyList<SchematicItemOperation> operations;
            try { operations = SchematicHierarchyDelta.Plan(actual, wanted, token); }
            catch (AutomationException error) { return [error.Code + ": " + error.Message]; }
            var existing = actual.Instances.SelectMany(screen => SchematicItemDelta.Index(screen.Items)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
            var result = new List<string>();
            foreach (var operation in operations)
            {
                if (operation.Update is { } packed)
                {
                    var (id, item) = SchematicItemDelta.Index([packed]).Single();
                    var after = JsonNode.Parse(SchematicJson.Formatter.Format(item))!.AsObject();
                    var before = existing.TryGetValue(id, out var old) ? JsonNode.Parse(SchematicJson.Formatter.Format(old))!.AsObject() : new JsonObject();
                    var fields = after.Select(p => p.Key).Union(before.Select(p => p.Key))
                        .Where(k => (after[k]?.ToJsonString() ?? "") != (before[k]?.ToJsonString() ?? ""))
                        .Select(k => k + ": " + Short(before[k]?.ToJsonString()) + " -> " + Short(after[k]?.ToJsonString()));
                    result.Add("update " + item.Descriptor.Name + " " + id.ToString("D") + " {" + string.Join(", ", fields) + "}");
                }
                else if (operation.Create is { } created)
                    result.Add("missing " + SchematicItemDelta.Index([created]).Single().Value.Descriptor.Name + " " + Short(SchematicJson.Formatter.Format(operation)));
                else result.Add(operation.OperationCase + " " + Short(SchematicJson.Formatter.Format(operation)));
            }
            return result;
            static string Short(string? text) => text is null ? "(none)" : text.Length <= 300 ? text : text[..300] + "...";
        }

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

        // Every symbol's body, pins and visible fields lie inside its sheet's usable region and no two symbol bodies on one
        // sheet overlap (§1.6.3, NativePresentationChecks in KiCad); each sheet is rendered as retained evidence.
        async Task CheckPresentation(CheckedSchematicState state, SchematicDesign design, string name)
        {
            var bindings = design.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
            var occurrences = design.Engineering.Circuit.Symbols;
            var owners = design.Engineering.Circuit.Components.ToDictionary(c => c.Id);
            var sheetOf = expected.Sheets.ToDictionary(s => s.ModelSheetInstance, s => s.Key);
            foreach (var sheet in expected.Sheets)
            {
                var screen = state.Electrical.Hierarchy.Data.Instances.Single(s => SheetPathKey(s.Metadata.Document) == sheetPaths[sheet.Key]);
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = screen.Metadata.Document.Clone() }, token);
                var ids = occurrences.Where(o => sheetOf[o.EffectiveSheetInstanceId(owners[o.ComponentId])] == sheet.Key)
                    .Select(o => bindings[o.Id]).ToArray();
                if (ids.Length > 0)
                {
                    var issues = await NativePresentationChecks.CheckSymbolPlacementAsync(client, screen.Metadata.Document, ids, usable[sheet.Key], token);
                    Assert.AreEqual(0, issues.Count, $"{name} {sheet.Key}: " + string.Join("; ", issues.Select(i => i.Code + " " + string.Join(" and ", i.Symbols))));
                }
                var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = screen.Metadata.Document.Clone() }, token);
                await File.WriteAllBytesAsync(Evidence(name + "-render-" + sheet.Key.ToLowerInvariant() + ".png"), image.Preview.Png.ToByteArray(), token);
            }
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        }

        // The recorded realization's batch as the executor journals it, plus one foreign wire before its assertion that joins
        // the VIN and DCDC_OUT labels on PSU. Resuming it through apply sends it to KiCad, whose assertion refuses it without
        // any change; the executor abandons the pending request and reports realization_connectivity_mismatch.
        async Task<object> RequireForcedMismatch(CheckedSchematicState at, byte[] xml)
        {
            var before = store.Read()!;
            var labels = prepared.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true)
                .Select(o => (o.TargetDocument, Label: o.Create.Unpack<LocalLabel>())).ToArray();
            var vin = labels.First(l => l.Label.Text.Text_ == "VIN");
            var dcdc = labels.First(l => l.Label.Text.Text_ == "DCDC_OUT" && l.TargetDocument.Equals(vin.TargetDocument));
            var foreign = new SchematicItemOperation { TargetDocument = vin.TargetDocument.Clone(), Create = Any.Pack(new SchematicLine
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = vin.Label.Position.Clone(), End = dcdc.Label.Position.Clone(),
                Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked
            }) };
            var batch = new ApplySchematicItemBatch { Document = at.State.Document.Clone(), DocumentEpoch = at.State.Revision.Epoch,
                ExpectedRevision = at.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                OriginId = before.State.OriginId.ToString("D"), Description = SchematicConnectionRealizer.BatchDescription };
            batch.Operations.Add(prepared.Operations.Take(prepared.Operations.Count - 1).Select(o => o.Clone()));
            batch.Operations.Add(foreign);
            batch.Operations.Add(prepared.Operations[^1].Clone());
            Guid request = Guid.NewGuid();
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document.Clone() }, token);
            store.Save(before.State with { PendingMutation = batch, PendingNativeState = at.State.Clone(),
                PendingLayout = DesignLayoutIntent.Create(path, xml, prepared.PlannedDesignFileBytes, request, before.RevisionToken,
                    DesignLayoutIntent.ConnectionRealizationLane) }, before.RevisionToken);
            var refused = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath = path,
                expectedRevisionToken = before.RevisionToken, operationId = request.ToString("D") });
            await File.WriteAllTextAsync(Evidence("mismatch-apply.json"), refused.GetRawText(), token);
            Assert.IsTrue(refused.TryGetProperty("isError", out var failed) && failed.GetBoolean(), refused.GetRawText());
            var content = refused.GetProperty("structuredContent");
            Assert.AreEqual(SchematicConnectionErrors.RealizationConnectivityMismatch, content.GetProperty("errorCode").GetString(), refused.GetRawText());
            string message = content.GetProperty("errorMessage").GetString()!;
            StringAssert.Contains(message, SchematicConnectionErrors.ConnectivityPostconditionFailed + ":", "The refusal carries KiCad's own detail.");
            StringAssert.Contains(message, "unexpected_join", "KiCad names the foreign join.");
            // Nothing was committed: KiCad's state, drawing and change journal, the XML and the recovery baseline are unchanged,
            // and the abandoned request is no longer pending.
            var after = await Capture();
            Assert.AreEqual(at.State, after.State, "The refused batch changes no revision, digest or dirty flag.");
            Assert.IsTrue(Same(at.Electrical.Hierarchy.Data, after.Electrical.Hierarchy.Data), "The refused batch draws nothing.");
            Assert.AreEqual(journal, await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document.Clone() }, token),
                "The refused batch adds no journal entry.");
            CollectionAssert.AreEqual(xml, await File.ReadAllBytesAsync(path, token), "No XML is published.");
            var abandoned = store.Read()!.State;
            Assert.IsFalse(abandoned.HasPendingWork, "The rejected request is abandoned.");
            Assert.AreEqual(SchematicDesignXml.Write(before.State.Baseline, []), SchematicDesignXml.Write(abandoned.Baseline, []), "The baseline does not advance.");
            CollectionAssert.AreEqual(before.State.DesiredFileBytes, abandoned.DesiredFileBytes);
            Assert.AreEqual(before.State.NativeRevision, abandoned.NativeRevision);
            return new { errorCode = content.GetProperty("errorCode").GetString(), message, nativeStateUnchanged = true, journalUnchanged = true,
                xmlUnchanged = true, baselineUnchanged = true, pendingAbandoned = true };
        }

        // CN-1 §10 and §6 once more on the realized design: I2C pull-ups R2 (PSU_SCL to RAIL_B) and R4 (MEM_SCL to RAIL_B)
        // coordinate-free, and R3 (PSU_SDA to RAIL_B) placed explicitly and locked. The layout tool aims R2 and R4 at the pins
        // they connect to; apply creates the three resistors and connects their six pins in one verified KiCad commit. Nothing
        // that existed before is changed, and KiCad's pin partition is exactly the XML nets.
        async Task<object> RequirePullUpRealization(CheckedSchematicState before)
        {
            var current = store.Read()!;
            Assert.IsFalse(current.State.HasPendingWork);
            var (connected, added) = WithPullUps(current.State.Baseline, expected, new SymbolPlacement(215.9m, 76.2m, 0, false, false, true));
            var written = Write(current, connected);
            var screenOf = expected.Sheets.ToDictionary(s => s.Key, s => s.NativeScreen);
            var pullRegions = regions.Where(r => r.ScreenId == screenOf["PSU"] || r.ScreenId == screenOf["CPU"]).ToArray();
            var proposed = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = written.RevisionToken, gridNm = PsuCpuLayoutPolicy.GridNm, clearanceNm = PsuCpuLayoutPolicy.ClearanceNm,
                pageInsetNm = PsuCpuLayoutPolicy.PageInsetNm, regions = pullRegions, userInstructions = "Place the I2C pull-ups next to the bus pins they serve." });
            await File.WriteAllTextAsync(Evidence("pull-up-layout.json"), RetainedToolEvidence(proposed), token);
            RequireToolSuccess(proposed);
            var proposal = proposed.GetProperty("structuredContent");
            Assert.IsTrue(proposal.GetProperty("canPropose").GetBoolean(), proposed.GetRawText());
            Assert.AreEqual(2, proposal.GetProperty("preferredAnchors").GetArrayLength(), "The two coordinate-free pull-ups are aimed at their partners.");
            var placed = SchematicDesignXml.Read(proposal.GetProperty("desiredXml").GetString()!, []);
            Assert.AreEqual(new SymbolPlacement(215.9m, 76.2m, 0, false, false, true),
                placed.Engineering.Circuit.Symbols.Single(s => s.Id == added["R3"].Occurrence).Placement, "R3 keeps its explicit, locked position.");
            Write(store.Read()!, placed);
            var preview = await Preview("pull-up");
            Assert.AreEqual(JsonValueKind.Null, preview.GetProperty("candidateDesignXml").ValueKind, "The pull-ups are a connected addition to realize.");
            var pullPlan = SchematicSynchronizationPlanner.Plan(store.Read()!.State, session, token);
            var pullIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(pullPlan);
            Assert.HasCount(3, pullIntent.CreatedSymbolIds, "The three pull-ups are created by the realization batch.");
            var reply = await Apply("pull-up");
            Assert.IsTrue(reply.GetProperty("nativeMutationCommitted").GetBoolean(), reply.GetRawText());
            Assert.IsTrue(reply.GetProperty("nativeReceipt").GetProperty("result").GetProperty("connectivityAssertionVerified").GetBoolean(), reply.GetRawText());
            var after = await Capture();
            var design = store.Read()!.State.Baseline;
            var comparison = SchematicElectricalComparison.Compare(design, after.Electrical, [], token);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent,
                "KiCad's pin partition is exactly the XML nets with the pull-ups: " + string.Join("; ", comparison.Differences.Select(d => d.Kind)));
            // Only creations: the three resistors and the generated items for their six pins; nothing existing changes.
            var delta = SchematicHierarchyDelta.Plan(before.Electrical.Hierarchy.Data, after.Electrical.Hierarchy.Data, token);
            Assert.IsTrue(delta.All(o => o.Create is not null || o.ReplaceLibraryCache is not null),
                "Realizing the pull-ups only creates items: " + string.Join(", ", delta.Select(o => o.OperationCase)));
            var symbols = SchematicModelProjection.NativeSymbols(design, after.Electrical.Hierarchy.Data);
            foreach (var reference in new[] { "R2", "R3", "R4" })
                Assert.IsTrue(symbols.ContainsKey(added[reference].Occurrence), reference + " is drawn.");
            var stubbed = await RequireLabelStubs(client, before, after, policy, token);
            await CheckPresentation(after, design, "pull-up");
            return new { preferredAnchors = proposal.GetProperty("preferredAnchors").GetArrayLength(), created = 3,
                createdItems = delta.Count, labelStubs = stubbed };
        }
    }

    // CN-1 §6 on KiCad's own result, measured by KiCad at the realized revision. Everything the realization added (compared with
    // `before`) is a wire or a local or hierarchical label, or a sheet pin on an existing sheet symbol: no global label, junction,
    // symbol or other item. Every wire is a straight stub of 2, 3, 4, 6 or 8 connection-grid steps that leaves a pin away from its
    // body, or a new sheet pin away from its sheet, and ends in exactly one new label turned the same way. A pin stub's label text
    // is the name KiCad gives that pin's net (its last path part); a sheet pin's label and the sheet pin carry the same text.
    // Hierarchical labels appear only on child sheets; every new sheet pin sits on the left or right edge of its sheet symbol and
    // faces into it. Returns the counts per sheet and kind.
    private static async Task<object> RequireLabelStubs(NativeClient client, CheckedSchematicState before, CheckedSchematicState after,
        SchematicConnectionPolicy policy, CancellationToken token)
    {
        var lengths = SchematicConnectionPolicy.StubMultiples.Select(m => m * policy.GridNm).ToHashSet();
        var netOf = new Dictionary<(string Path, string Item), string>();
        foreach (var net in after.Electrical.Nets)
            foreach (var sheet in net.Sheets)
                foreach (var item in sheet.Items)
                    netOf[(string.Join('/', sheet.Path.Path.Select(p => p.Value)), item.Value)] = net.Name;
        string Leaf(string name) => name[(name.LastIndexOf('/') + 1)..];
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var screen in after.Electrical.Hierarchy.Data.Instances)
        {
            string path = SheetPathKey(screen.Metadata.Document);
            bool child = screen.Metadata.Document.SheetPath.Path.Count > 1;
            var old = before.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(screen.Metadata.Document));
            var oldItems = SchematicItemDelta.Index(old.Items);
            var items = SchematicItemDelta.Index(screen.Items);
            var added = items.Where(p => !oldItems.ContainsKey(p.Key)).ToArray();
            var oldPins = oldItems.Values.OfType<SheetSymbol>().SelectMany(s => s.Pins).Select(p => p.Id.Value).ToHashSet(StringComparer.Ordinal);
            var sheetPins = items.Values.OfType<SheetSymbol>().SelectMany(s => s.Pins.Where(p => !oldPins.Contains(p.Id.Value)).Select(p => (Sheet: s, Pin: p))).ToArray();
            foreach (var (_, item) in added)
                Assert.IsTrue(item is SchematicLine { Type: SchematicLineType.SltWire } or LocalLabel or HierarchicalLabel or SchematicSymbolInstance,
                    path + ": a realization adds only wires, local and hierarchical labels (and the symbols it creates), not " + item.Descriptor.FullName);
            Assert.IsTrue(child || !added.Any(p => p.Value is HierarchicalLabel), path + ": hierarchical labels only on child sheets.");
            var wires = added.Select(p => p.Value).OfType<SchematicLine>().ToArray();
            var labels = added.Where(p => p.Value is LocalLabel or HierarchicalLabel).Select(p => (Id: p.Key, Item: p.Value)).ToArray();
            if (wires.Length == 0 && labels.Length == 0 && sheetPins.Length == 0) continue;
            var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = screen.Metadata.Document.Clone(), ExpectedRevision = after.State.Revision.Clone() }, token);
            var pins = measured.Obstacles.Where(o => o.SymbolPins is not null).SelectMany(o => o.SymbolPins.Pins)
                .ToLookup(p => (p.Position.XNm, p.Position.YNm));
            var labelled = new HashSet<Guid>();
            var stubbedSheetPins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var wire in wires)
            {
                var (sx, sy, ex, ey) = (wire.Start.XNm, wire.Start.YNm, wire.End.XNm, wire.End.YNm);
                Assert.IsTrue(sx == ex || sy == ey, path + ": a stub is straight along one axis.");
                long length = Math.Abs(ex - sx) + Math.Abs(ey - sy);
                Assert.IsTrue(lengths.Contains(length), $"{path}: stub length {length} nm is not 2, 3, 4, 6 or 8 connection-grid steps.");
                // Which end the stub leaves from: a pin or a new sheet pin; the label sits on the other end.
                bool FromPin((long X, long Y) at) => pins[at].Any();
                var startSheetPin = sheetPins.Where(s => s.Pin.Position.XNm == sx && s.Pin.Position.YNm == sy).ToArray();
                var endSheetPin = sheetPins.Where(s => s.Pin.Position.XNm == ex && s.Pin.Position.YNm == ey).ToArray();
                bool fromStart = FromPin((sx, sy)) || startSheetPin.Length != 0, fromEnd = FromPin((ex, ey)) || endSheetPin.Length != 0;
                Assert.IsTrue(fromStart != fromEnd, $"{path}: a stub has exactly one end on a pin or sheet pin ({sx},{sy})-({ex},{ey}).");
                var (ax, ay, bx, by) = fromStart ? (sx, sy, ex, ey) : (ex, ey, sx, sy);
                var direction = (Math.Sign(bx - ax), Math.Sign(by - ay));
                var at = labels.Where(l => Position(l.Item) == (bx, by)).ToArray();
                Assert.HasCount(1, at, $"{path}: exactly one new label ends the stub at ({bx},{by}).");
                var label = at[0];
                labelled.Add(label.Id);
                Assert.AreEqual(SchematicConnectionGeometry.Spin(direction), Spin(label.Item), $"{path}: the label faces away from its pin.");
                string text = Text(label.Item);
                var sheetPin = fromStart ? startSheetPin : endSheetPin;
                if (sheetPin.Length != 0)
                {
                    Assert.HasCount(1, sheetPin);
                    var (sheet, pin) = sheetPin[0];
                    var side = pin.Side;
                    Assert.AreEqual(side == SheetSide.ShsLeft ? (-1, 0) : (1, 0), direction, $"{path}: a sheet-pin stub leaves its sheet outward.");
                    Assert.AreEqual(pin.Text.Text_, text, $"{path}: a sheet-pin stub's label carries the sheet pin's name.");
                    Assert.IsTrue(stubbedSheetPins.Add(pin.Id.Value), $"{path}: one stub per new sheet pin.");
                    Count(label.Item is HierarchicalLabel ? "sheet-pin-stub-hierarchical" : "sheet-pin-stub-local");
                }
                else
                {
                    var anchors = pins[(ax, ay)].ToArray();
                    Assert.IsTrue(anchors.Any(p => SchematicConnectionGeometry.Outward(p) == direction), $"{path}: the stub leaves pin at ({ax},{ay}) away from its body.");
                    var named = anchors.Select(p => netOf.GetValueOrDefault((path, p.Id.Value))).OfType<string>().Distinct().ToArray();
                    Assert.HasCount(1, named, $"{path}: the pin at ({ax},{ay}) is in one KiCad net.");
                    Assert.AreEqual(Leaf(named[0]), text, $"{path}: the label names the net KiCad reports for its pin.");
                    Count(label.Item is HierarchicalLabel ? "hierarchical-stub" : "local-stub");
                }
            }
            Assert.AreEqual(labels.Length, labelled.Count, path + ": every new label ends a new stub.");
            foreach (var (sheet, pin) in sheetPins)
            {
                Assert.IsTrue(stubbedSheetPins.Contains(pin.Id.Value), $"{path}: new sheet pin {pin.Text.Text_} has its stub.");
                Assert.AreEqual(pin.Side == SheetSide.ShsLeft ? sheet.Position.XNm : sheet.Position.XNm + sheet.Size.XNm, pin.Position.XNm,
                    $"{path}: sheet pin {pin.Text.Text_} sits on its sheet's edge.");
                Assert.AreEqual(pin.Side == SheetSide.ShsLeft ? SchematicLabelSpinStyle.SlssRight : SchematicLabelSpinStyle.SlssLeft, pin.SpinStyle,
                    $"{path}: sheet pin {pin.Text.Text_} faces into its sheet.");
                Assert.IsTrue(pin.Side is SheetSide.ShsLeft or SheetSide.ShsRight, $"{path}: sheet pins go on the left or right edge.");
                Count("sheet-pin");
            }
            counts[path + " wires"] = wires.Length;
        }
        return counts;

        static (long, long) Position(IMessage item) => item switch
        {
            LocalLabel l => (l.Position.XNm, l.Position.YNm), HierarchicalLabel h => (h.Position.XNm, h.Position.YNm),
            _ => throw new ArgumentException("Not a label.")
        };
        static SchematicLabelSpinStyle Spin(IMessage item) => item switch
        {
            LocalLabel l => l.SpinStyle, HierarchicalLabel h => h.SpinStyle, _ => throw new ArgumentException("Not a label.")
        };
        static string Text(IMessage item) => item switch
        {
            LocalLabel l => l.Text.Text_, HierarchicalLabel h => h.Text.Text_, _ => throw new ArgumentException("Not a label.")
        };
    }

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

        var (regions, usable, sheetPaths) = await PsuCpuRegions(client, seedBaseline, expected, seeded.State.Revision, token);

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

        // Connection-aware placement of the whole Complete stage on this seed (CN-1 §10), read-only.
        var completeLayout = await RequirePsuCpuConnectedCompleteLayout(client, context, store, path, instanceId, regions, Evidence, token);

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
        // Connection-aware placement of pull-ups added to the created stage (CN-1 §10), read-only.
        var connectedAddition = await RequirePsuCpuConnectedAddition(client, context, store, path, instanceId, host, expected, sheetPaths,
            regions, Evidence, token);

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

        // Presentation findings across every sheet instance of the created hierarchy, through the public MCP tool.
        var presentationFindings = await VerifyPsuCpuPresentationFindings(client, host, instanceId, document, expected, sheetPaths,
            synchronized.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId), processId, display, Evidence, token);

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
            settledPlan, presentation = expected.Presentation, presentationFindings, crossPlatformReady = false, connectionIntent,
            connectedPlacement = new { completeStage = completeLayout, addition = connectedAddition }
        }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

        // CN-1 connection intent for the fixture's Complete stage (cn1-wiring-intent.md §5), planned from this editor's
        // real S1 capture, the part symbols it captured and the laid-out placements, with this editor's own handshake, which
        // advertises schematic.connection-realization.v1. The plan is inspected against expected-native.json: every net,
        // crossing, hierarchical label, sheet pin and native group. Without a handshake the same revision stays on the general
        // path, which refuses it (its code is recorded). Drawing the connections is the NativeConnectedRealization journey;
        // nothing here is saved, published or sent to the editor.
        async Task<object> RequirePsuCpuConnectionIntent(SchematicDesign layout)
        {
            var session = await client.HandshakeAsync(token);
            Assert.AreEqual(instanceId, session.InstanceId);
            var advertised = RequireRealizationAdvertised(session);
            var current = store.Read()!;
            var nativeBefore = await Capture();
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            var nets = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets;
            var connected = layout with { Engineering = layout.Engineering with { Circuit = layout.Engineering.Circuit with { Nets = nets } } };
            var revision = current.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(connected, [])) };
            var gated = SchematicSynchronizationPlanner.Plan(revision, token);
            Assert.IsNull(gated.Connections, "Without a handshake the planner admits no wiring.");
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
                { withoutHandshakeErrorCode = gated.ErrorCode, intent = SchematicConnectionIntentBuilder.Summary(intent), stackedSplit = refused }), token);
            return new { withoutHandshakeErrorCode = gated.ErrorCode, nets = intent.Nets.Count, islands = intent.Screens.Sum(s => s.Islands.Count),
                ports = intent.Ports.Count, expectedGroups = intent.ExpectedGroups.Count, createdSymbols = intent.CreatedSymbolIds.Count,
                stackedSplit = refused };
        }

        // The same rules once KiCad shows the created components with U2's stacked pins joined: splitting them over two nets is
        // refused before anything is sent, while the frozen Complete nets (both pins on RAIL_B) still plan over KiCad's joined
        // pair, predicting it inside RAIL_B's native group. Nothing is saved, published or sent to the editor.
        async Task<object> RequireStackedPinsOnCreatedSymbols()
        {
            var session = await client.HandshakeAsync(token);
            var advertised = RequireRealizationAdvertised(session);
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

    // ---- presentation findings across a hierarchy (ledger p0cd7a559e13ec029) ----

    /// <summary>The created PSU/CPU hierarchy checked through the public kicad_schematic_check_presentation tool with
    /// includeSubsheets, while KiCad keeps displaying the root. First every sheet instance (root, PSU, CPU, CPU_POWER)
    /// must report nothing. Then real defects are made on the PSU child sheet in one native batch: U4 moved onto U3 and U2
    /// (overlapping bodies), J1 moved past the left page edge (page overflow), U1's value turned upside down, U2's
    /// reference hidden, R1's reference unannotated ("R?"), a new wire crossing three unrelated wires, and a label moved
    /// onto U2's body. Beside them sit intentional patterns that must stay quiet: a label anchored exactly on a U3 pin end,
    /// a power symbol with a hidden '#PWR01' reference whose pin ends exactly on a U1 pin end, and a global label with a
    /// field. The report must list exactly the expected PSU findings, each with its objects, measured value, threshold and
    /// unit, and nothing on the untouched root, CPU and CPU_POWER sheets. KiCad's own painted pixels then confirm the
    /// measured field glyphs at 0, 90 and 180 degrees, on a symbol rotated 90 degrees and on a global label, drawn with the
    /// renderer's default pen. A check started from the PSU child sheet reports the same findings, one started from CPU
    /// covers only CPU and CPU_POWER, and a stale revision or an unloaded sheet is refused by name. KiCad's Undo shortcut
    /// then removes the defects, the hierarchy reports nothing again, and the saved sheets are reloaded so the editor ends
    /// as it began.</summary>
    private static async Task<object> VerifyPsuCpuPresentationFindings(NativeClient client, IMcpToolClient host, string instanceId,
        DocumentSpecifier root, PsuCpuExpectedNative expected, IReadOnlyDictionary<string, string> sheetPaths,
        IReadOnlyDictionary<Guid, Guid> nativeSymbols, int processId, string display, Func<string, string> evidence, CancellationToken token)
    {
        const long Grid = 1_270_000, Tolerance = 500_000;
        const string Mm = PresentationUnits.Millimetres, Degrees = PresentationUnits.Degrees, Count = PresentationUnits.Count;
        string Key(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
        Task<CheckedSchematicState> State() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        Task<GetOpenDocumentsResponse> Displayed() => client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
            new() { Type = (DocumentType)1 }, token);
        var sheetNames = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["ROOT"] = "/", ["PSU"] = "/PSU/", ["CPU"] = "/CPU/", ["CPU_POWER"] = "/CPU/CPU_POWER/" };
        CollectionAssert.AreEquivalent(sheetNames.Keys.ToArray(), sheetPaths.Keys.ToArray());
        string[] allSheets = [.. sheetNames.Keys];

        Task<JsonElement> Call(DocumentSpecifier start, KiCad.Automation.Protocol.DocumentRevision? expectedRevision, bool subsheets = true,
            decimal? overlapToleranceMm = null)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId, ["documentJson"] = JsonFormatter.Default.Format(start),
                ["minimumTextHeightMm"] = 1m, ["maximumTextHeightMm"] = 3m, ["includeSubsheets"] = subsheets
            };
            if (expectedRevision is not null) arguments["expectedRevisionJson"] = JsonFormatter.Default.Format(expectedRevision);
            if (overlapToleranceMm is not null) arguments["overlapToleranceMm"] = overlapToleranceMm;
            return host.Tool("kicad_schematic_check_presentation", arguments);
        }

        // One call of the public tool over STDIO from `start`, which must cover exactly `sheets`. Checking never edits the
        // design, its revision or the displayed sheet; the report names the revision KiCad holds and every finding names it
        // too, with its sheet name and, whenever it measures something, the measured value, the threshold and their unit.
        async Task<(JsonElement Findings, JsonElement Targets, JsonElement Limitations, CheckedSchematicState State)> Check(string name,
            DocumentSpecifier start, IReadOnlyCollection<string> sheets, KiCad.Automation.Protocol.DocumentRevision? expectedRevision = null)
        {
            var state = await State();
            var displayed = await Displayed();
            var result = await Call(start, expectedRevision);
            await File.WriteAllTextAsync(evidence("presentation-" + name + ".json"), result.GetRawText(), token);
            RequireToolSuccess(result);
            Assert.AreEqual(state, await State(), name + ": checking presentation changes neither the design nor its revision.");
            Assert.AreEqual(displayed, await Displayed(), name + ": checking presentation never changes the displayed sheet.");
            var check = result.GetProperty("structuredContent").GetProperty("check");
            var report = check.GetProperty("report");
            var covered = report.GetProperty("sheets").EnumerateArray().ToDictionary(s => s.GetProperty("sheetPath").GetString()!,
                s => s.GetProperty("sheetName").GetString());
            CollectionAssert.AreEquivalent(sheets.Select(s => sheetPaths[s]).ToArray(), covered.Keys.ToArray(),
                name + ": exactly the sheet instances from the start sheet down are checked.");
            foreach (var sheet in sheets) Assert.AreEqual(sheetNames[sheet], covered[sheetPaths[sheet]], name);
            string epoch = state.State.Revision.Epoch; ulong sequence = state.State.Revision.Sequence;
            Assert.AreEqual(epoch, report.GetProperty("revision").GetProperty("epoch").GetString(), name);
            Assert.AreEqual(sequence, report.GetProperty("revision").GetProperty("sequence").GetUInt64(), name);
            Assert.IsFalse(report.GetProperty("clear").GetBoolean(), name + ": partial coverage is never a verification pass.");
            Assert.IsTrue(check.GetProperty("limitations").GetArrayLength() > 0, name);
            // The report states the policy it applied, so a report without findings still says which tolerance let objects touch.
            var applied = report.GetProperty("policy");
            Assert.AreEqual(1m, applied.GetProperty("minimumTextHeightMm").GetDecimal(), name);
            Assert.AreEqual(3m, applied.GetProperty("maximumTextHeightMm").GetDecimal(), name);
            Assert.AreEqual(2, applied.GetProperty("maximumCrossingsPerSignal").GetInt32(), name);
            Assert.AreEqual(PresentationPolicy.DefaultOverlapToleranceMm, applied.GetProperty("overlapToleranceMm").GetDecimal(), name);
            var findings = report.GetProperty("findings");
            Assert.AreEqual(1, findings.EnumerateArray().Count(f => f.GetProperty("rule").GetString() == "coverage_incomplete"
                && f.GetProperty("sheetPath").GetString() == ""), name);
            foreach (var finding in findings.EnumerateArray())
            {
                Assert.AreEqual(epoch, finding.GetProperty("revision").GetProperty("epoch").GetString(), name);
                Assert.AreEqual(sequence, finding.GetProperty("revision").GetProperty("sequence").GetUInt64(), name);
                if (finding.GetProperty("sheetPath").GetString() is not { Length: > 0 } path) continue;
                Assert.AreEqual(covered[path], finding.GetProperty("sheetName").GetString(), name);
                Assert.IsTrue(Number(finding, "measured") is not null && Number(finding, "limit") is not null
                    && finding.GetProperty("unit").ValueKind == JsonValueKind.String,
                    name + ": every finding names its measured value, threshold and unit: " + Describe([finding]));
            }
            return (findings, check.GetProperty("repairTargets"), check.GetProperty("limitations"), state);
        }
        // A refused check changes nothing and names why.
        async Task<string> Refused(string name, DocumentSpecifier start, KiCad.Automation.Protocol.DocumentRevision? expectedRevision,
            bool subsheets = true, decimal? overlapToleranceMm = null)
        {
            var state = await State();
            var result = await Call(start, expectedRevision, subsheets, overlapToleranceMm);
            await File.WriteAllTextAsync(evidence("presentation-" + name + ".json"), result.GetRawText(), token);
            Assert.IsTrue(result.TryGetProperty("isError", out var error) && error.GetBoolean(), name + ": " + result.GetRawText());
            Assert.AreEqual(state, await State(), name + ": a refused check changes nothing.");
            using var body = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            return body.RootElement.GetProperty("code").GetString()!;
        }
        static IEnumerable<JsonElement> On(JsonElement findings, string path) =>
            findings.EnumerateArray().Where(f => f.GetProperty("sheetPath").GetString() == path);
        static string Describe(IEnumerable<JsonElement> findings) => string.Join("; ", findings.Select(f => f.GetProperty("rule").GetString()
            + " " + f.GetProperty("sheetName").GetString() + " [" + string.Join(",", f.GetProperty("objectIds").EnumerateArray().Select(i => i.GetString())) + "] "
            + f.GetProperty("measured") + "/" + f.GetProperty("limit") + " " + (f.TryGetProperty("unit", out var unit) ? unit.ToString() : "") + " "
            + f.GetProperty("message").GetString()));
        static Guid[] Ids(JsonElement finding) => [.. finding.GetProperty("objectIds").EnumerateArray().Select(i => Guid.Parse(i.GetString()!))];
        static decimal? Number(JsonElement finding, string property) =>
            finding.GetProperty(property).ValueKind == JsonValueKind.Number ? finding.GetProperty(property).GetDecimal() : null;
        static PresentationBounds Bounds(JsonElement finding)
        {
            var bounds = finding.GetProperty("bounds");
            return new(bounds.GetProperty("leftNm").GetInt64(), bounds.GetProperty("topNm").GetInt64(),
                bounds.GetProperty("rightNm").GetInt64(), bounds.GetProperty("bottomNm").GetInt64());
        }
        static PresentationBounds Box(Box2 box) => new(box.Position.XNm, box.Position.YNm,
            box.Position.XNm + box.Size.XNm, box.Position.YNm + box.Size.YNm);
        static string Fixed(decimal value) => value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
        // Field runtime identities are not persistent: find a field through its owner and field name in that same report.
        static Guid Field(JsonElement targets, string path, Guid owner, string name) => Guid.Parse(targets.EnumerateArray().Single(t =>
            t.GetProperty("sheetPath").GetString() == path && t.GetProperty("ownerId").GetString() == owner.ToString("D")
            && t.GetProperty("fieldName").GetString() == name).GetProperty("objectId").GetString()!);
        // A finding as rule, objects (a field named by its owner and field name), measured value, threshold and unit.
        static string[] Canonical(IEnumerable<JsonElement> findings, JsonElement targets)
        {
            var names = targets.EnumerateArray().Where(t => t.GetProperty("fieldName").ValueKind == JsonValueKind.String)
                .ToDictionary(t => t.GetProperty("objectId").GetString()!, t => t.GetProperty("ownerId").GetString() + "." + t.GetProperty("fieldName").GetString());
            return [.. findings.Select(f => f.GetProperty("rule").GetString() + " [" + string.Join(",", f.GetProperty("objectIds").EnumerateArray()
                .Select(i => names.GetValueOrDefault(i.GetString()!, i.GetString()!)).Order(StringComparer.Ordinal)) + "] "
                + Fixed(Number(f, "measured") ?? -1) + "/" + Fixed(Number(f, "limit") ?? -1) + " " + f.GetProperty("unit").GetString())
                .Order(StringComparer.Ordinal)];
        }

        // The created layout breaks none of the measured rules on any sheet instance: only the report-level coverage notice
        // remains. The drawing-sheet frame is not measured (a stated limitation), so this is not a readability certificate.
        var clean = await Check("clean", root, allSheets);
        var unexpected = clean.Findings.EnumerateArray().Where(f => f.GetProperty("sheetPath").GetString() != "").ToArray();
        Assert.IsEmpty(unexpected, "The created PSU/CPU layout must report nothing on any sheet: " + Describe(unexpected));
        Assert.IsTrue(clean.Limitations.EnumerateArray().Any(l => l.GetString()!.Contains("drawing-sheet frame", StringComparison.Ordinal)),
            "What the check does not measure is stated with its result.");

        string psuPath = sheetPaths["PSU"];
        DocumentSpecifier Instance(string sheet) => clean.State.Electrical.Hierarchy.Data.Instances
            .Single(s => Key(s.Metadata.Document) == sheetPaths[sheet]).Metadata.Document;
        var psu = Instance("PSU");
        Guid Symbol(string reference) => nativeSymbols[expected.Symbols.Single(s => s.Sheet == "PSU" && s.Reference == reference).Occurrence];
        Guid j1 = Symbol("J1"), u1 = Symbol("U1"), r1 = Symbol("R1"), u2 = Symbol("U2"), u3 = Symbol("U3"), u4 = Symbol("U4");
        // KiCad's own measurement of this instance, at the observed revision, through the native primitive the tool uses.
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            { Document = psu.Clone(), ExpectedRevision = clean.State.State.Revision.Clone(), IncludePresentation = true }, token);
        Assert.AreEqual(psu, measured.Presentation.Document);
        Assert.AreEqual("/PSU/", measured.Presentation.SheetName);
        static PresentationBounds Body(SchematicPlacementGeometry facts, Guid id) => Box(facts.Presentation.Objects
            .Single(o => o.Id.Value == id.ToString("D") && o.PresentationRole == "symbol").Bounds);
        // The topmost visible pin of a symbol whose body lies in the given direction from its connection point.
        SchematicPinAnchor Pin(Guid symbol, int towardBodyX, int towardBodyY) => measured.Obstacles.Single(o => o.Id.Value == symbol.ToString("D"))
            .SymbolPins.Pins.Where(p => p.Visible && p.BodyDirectionX == towardBodyX && p.BodyDirectionY == towardBodyY)
            .OrderBy(p => p.Position.YNm).ThenBy(p => p.Position.XNm).First();
        var query = new GetItemsById { Header = new ItemHeader { Document = psu.Clone() } };
        foreach (var id in new[] { j1, u1, r1, u2, u4 }) query.Items.Add(new KIID { Value = id.ToString("D") });
        var originals = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items
            .Select(i => i.Unpack<SchematicSymbolInstance>()).ToDictionary(s => Guid.Parse(s.Id.Value));
        Assert.HasCount(5, originals);
        static void Shift(SchematicSymbolInstance symbol, long dx, long dy)
        {
            symbol.Position.XNm += dx; symbol.Position.YNm += dy;
            foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField, symbol.DatasheetField, symbol.DescriptionField }
                         .Concat(symbol.UserFields).Where(f => f?.Text?.Position is not null))
            {
                field.Text.Position.XNm += dx; field.Text.Position.YNm += dy;
            }
        }
        static SchematicSymbolInstance Moved(SchematicSymbolInstance symbol, long dx, long dy)
        {
            var moved = symbol.Clone(); Shift(moved, dx, dy); return moved;
        }

        async Task<SchematicViewSet> RenderPsu(string name)
        {
            var request = new RenderSchematicViews { Document = psu.Clone() };
            request.Views.Add(new SchematicRenderView { Key = "page", WidthPixels = 1600, HeightPixels = 1132,
                Region = measured.Presentation.PageBounds.Clone() });
            var rendered = await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token);
            var png = rendered.Views.Single().Preview.Png;
            Assert.IsTrue(png.Length > 0);
            await File.WriteAllBytesAsync(evidence(name + "-psu.png"), png.ToByteArray(), token);
            return rendered;
        }

        // U4's body corner onto the centre of U3's body, on the 1.27 mm grid; J1 at least 5.08 mm past the left page edge.
        PresentationBounds body2 = Body(measured, u2), body3 = Body(measured, u3), body4 = Body(measured, u4), body1 = Body(measured, j1);
        long dx4 = (long)Math.Round(((body3.LeftNm + body3.RightNm) / 2 - body4.LeftNm) / (double)Grid) * Grid;
        long dy4 = (long)Math.Round(((body3.TopNm + body3.BottomNm) / 2 - body4.TopNm) / (double)Grid) * Grid;
        var moved4 = new PresentationBounds(body4.LeftNm + dx4, body4.TopNm + dy4, body4.RightNm + dx4, body4.BottomNm + dy4);
        static PresentationBounds? Shared(PresentationBounds a, PresentationBounds b)
        {
            long left = Math.Max(a.LeftNm, b.LeftNm), top = Math.Max(a.TopNm, b.TopNm), right = Math.Min(a.RightNm, b.RightNm), bottom = Math.Min(a.BottomNm, b.BottomNm);
            return left < right && top < bottom ? new(left, top, right, bottom) : null;
        }
        // How deep two boxes overlap (the smaller side of their shared area) and how far a box reaches beyond the page, in mm.
        static decimal Depth(PresentationBounds a, PresentationBounds b) => Shared(a, b) is { } shared
            ? Math.Min(shared.RightNm - shared.LeftNm, shared.BottomNm - shared.TopNm) / 1_000_000m
            : throw new AssertFailedException($"{a} and {b} do not overlap.");
        static decimal Beyond(PresentationBounds page, PresentationBounds box) => Math.Max(Math.Max(page.LeftNm - box.LeftNm, page.TopNm - box.TopNm),
            Math.Max(box.RightNm - page.RightNm, box.BottomNm - page.BottomNm)) / 1_000_000m;
        var overlap = Shared(body3, moved4) ?? throw new AssertFailedException("The seeded U4 must cover U3.");
        Assert.IsTrue(Depth(body3, moved4) > Tolerance / 1_000_000m, "The seeded U4 must cover U3 by more than the overlap tolerance.");
        long dx1 = -((body1.LeftNm + 5_080_000 + Grid - 1) / Grid) * Grid;
        var moved1 = body1 with { LeftNm = body1.LeftNm + dx1, RightNm = body1.RightNm + dx1 };
        Assert.IsTrue(moved1.LeftNm <= -5_080_000);
        var upsideDown = originals[u1].Clone();
        // KiCad turns a field of a symbol rotated by 90 or 270 degrees to read upright; on a symbol drawn at 0 or 180 degrees
        // the field's own angle decides how it is painted.
        Assert.IsTrue(upsideDown.Transform is null || upsideDown.Transform.Orientation is SchematicSymbolOrientation.SsoUnknown
            or SchematicSymbolOrientation.Sso0 or SchematicSymbolOrientation.Sso180, "U1 orientation: " + upsideDown.Transform);
        upsideDown.ValueField.Text.Attributes.Angle = new() { ValueDegrees = 180 };
        var hidden = originals[u2].Clone(); hidden.ReferenceField.Visible = false;
        var unannotated = originals[r1].Clone(); unannotated.ReferenceField.Text.Text_ = "R?";
        foreach (var record in unannotated.InstanceRecords?.Records.Where(r => string.Join('/', r.Path.Select(p => p.Value)) == psuPath) ?? [])
            record.Reference = "R?";
        // Every probed field is drawn with the renderer's default pen: none sets its own stroke width.
        foreach (var field in new[] { originals[u1].ValueField, originals[u4].ValueField, originals[r1].ReferenceField, originals[r1].ValueField })
            Assert.AreEqual(0L, field.Text.Attributes.StrokeWidth?.ValueNm ?? 0, "Probed fields use the renderer's default pen.");

        // A new wire crossing three unrelated wires in the free strip above the title-block reserve, each with its own label.
        Vector2 Point(double xMm, double yMm) => new() { XNm = (long)Math.Round(xMm * 1_000_000), YNm = (long)Math.Round(yMm * 1_000_000) };
        Vector2 Snap(long xNm, long yNm) => new() { XNm = (long)Math.Round(xNm / (double)Grid) * Grid, YNm = (long)Math.Round(yNm / (double)Grid) * Grid };
        var created = new List<(SchematicLine Wire, LocalLabel Label)>();
        void Wire(Vector2 start, Vector2 end, string name)
        {
            var wire = new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") }, Type = SchematicLineType.SltWire,
                Start = start.Clone(), End = end.Clone(), Locked = LockedState.LsUnlocked };
            var label = new LocalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = start.Clone(),
                Text = new() { Text_ = name, Position = start.Clone(), Attributes = new() { Size = new() { XNm = 1_270_000, YNm = 1_270_000 } } } };
            created.Add((wire, label));
        }
        Wire(Point(20.32, 182.88), Point(81.28, 182.88), "PM");
        double[] crossings = [35.56, 50.8, 66.04];
        for (int i = 0; i < crossings.Length; i++) Wire(Point(crossings[i], 170.18), Point(crossings[i], 195.58), "PX" + i);

        // Labels exactly as connected realization generates them: one on the end of U3's topmost left pin, facing away from
        // U3 (intentional, must stay quiet), and one moved onto U2's body (must be reported); and a global label with a field
        // in the empty middle of the page, whose field KiCad paints with the global label's text offset.
        var policy = SchematicConnectionPolicy.FromSnapshot(clean.State.Electrical.Hierarchy.Data);
        var u3Pin = Pin(u3, 1, 0);
        Guid tapId = Guid.NewGuid(), onBodyId = Guid.NewGuid(), globalId = Guid.NewGuid();
        var tap = (LocalLabel)SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Local, tapId, u3Pin.Position, "U3_TAP",
            SchematicLabelSpinStyle.SlssLeft, policy);
        var onBody = (LocalLabel)SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Local, onBodyId,
            Snap(body2.LeftNm + 7_620_000, body2.TopNm + 3_810_000), "ON_U2", SchematicLabelSpinStyle.SlssRight, policy);
        var global = (GlobalLabel)SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Global, globalId, Point(148.59, 101.6),
            "PIXEL_PROBE", SchematicLabelSpinStyle.SlssRight, policy);
        global.Fields.Add(new SchematicField { Name = "Pixel note", Visible = true, Text = new() { Text_ = "offset note",
            Position = Point(148.59, 106.68), Attributes = new() { Size = new() { XNm = 1_270_000, YNm = 1_270_000 }, Multiline = true,
                HorizontalAlignment = HorizontalAlignment.HaLeft, VerticalAlignment = VerticalAlignment.VaCenter, Angle = new() { ValueDegrees = 0 } } } });

        // A power symbol built from R1's captured definition: global power, one visible power-input pin, value "+3V3" and
        // its reference "#PWR01" hidden as KiCad hides power references. It is rotated by 90 degrees so that its pin faces
        // U1's topmost right pin and ends exactly on that pin's end, the two bodies touching only there.
        var u1Pin = Pin(u1, -1, 0);
        SchematicSymbolInstance PowerProbe(Guid id, SchematicSymbolOrientation orientation, Vector2 position)
        {
            var power = originals[r1].Clone();
            power.Id = new() { Value = id.ToString("D") };
            power.Path = psu.SheetPath.Clone();
            Shift(power, position.XNm - power.Position.XNm, position.YNm - power.Position.YNm);
            power.Transform = new() { Orientation = orientation };
            power.Locked = LockedState.LsUnlocked;
            power.Definition.Type = SchematicSymbolType.SstGlobalPower;
            power.Definition.Id = new() { LibraryNickname = "presentation", EntryName = "PowerProbe" };
            power.LibraryId = power.Definition.Id.Clone(); power.LibName = "";
            power.Definition.ReferenceField.Text.Text_ = "#PWR"; power.Definition.ValueField.Text.Text_ = "+3V3";
            power.ReferenceField.Text.Text_ = "#PWR01"; power.ReferenceField.Visible = false;
            power.ReferenceField.Text.Position = position.Clone();
            power.ValueField.Text.Text_ = "+3V3";
            foreach (var child in power.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).ToArray())
            {
                var pin = child.Item.Unpack<SchematicPin>();
                if (pin.Number != "1") { power.Definition.Items.Remove(child); continue; }
                pin.Id = new() { Value = Guid.NewGuid().ToString("D") };
                if (pin.LibraryPinId is not null) pin.LibraryPinId = new() { Value = Guid.NewGuid().ToString("D") };
                pin.ElectricalType = ElectricalPinType.EptPowerInput;
                child.Item = Any.Pack(pin);
            }
            if (power.InstanceRecords is not null) foreach (var record in power.InstanceRecords.Records) record.Reference = "#PWR01";
            return power;
        }
        Assert.AreEqual(0, u1Pin.BodyDirectionY, "U1's probed pin is horizontal.");
        var orientations = new[] { SchematicSymbolOrientation.Sso90, SchematicSymbolOrientation.Sso270 }
            .Select(o => PowerProbe(Guid.NewGuid(), o, u1Pin.Position)).ToArray();
        var probe = new MeasureSchematicPlacement { Document = psu.Clone(), ExpectedRevision = clean.State.State.Revision.Clone() };
        probe.Candidates.Add(orientations);
        probe.ItemCandidates.Add(new[] { Any.Pack(tap), Any.Pack(onBody), Any.Pack(global) });
        var probed = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(probe, token);
        var facing = orientations.Select(o => (Symbol: o, Pin: probed.Candidates.Single(c => c.Id.Equals(o.Id)).SymbolPins.Pins.Single()))
            .Single(o => o.Pin.BodyDirectionX == -u1Pin.BodyDirectionX && o.Pin.BodyDirectionY == -u1Pin.BodyDirectionY);
        var powerAt = new Vector2 { XNm = u1Pin.Position.XNm + (u1Pin.Position.XNm - facing.Pin.Position.XNm),
            YNm = u1Pin.Position.YNm + (u1Pin.Position.YNm - facing.Pin.Position.YNm) };
        Guid powerId = Guid.Parse(facing.Symbol.Id.Value);
        var power = PowerProbe(powerId, facing.Symbol.Transform.Orientation, powerAt);
        // The value sits where a power symbol draws it: beyond its body, on the side away from its pin (presentation re-review
        // finding 2). A placed symbol's field positions are given unturned, relative to the symbol's position, and KiCad turns
        // them with the symbol's transform. So the value goes on the pin's axis opposite the pin, 6.35 mm from the symbol's
        // position: 3.81 mm past the far end of the resistor body (which reaches 2.54 mm from it), clear of the body however
        // wide KiCad paints "+3V3". KiCad turns it with the symbol and then to read left to right. The value does not touch its
        // own body, so no owner exemption keeps it quiet, and it must report nothing.
        var probePin = power.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).Select(c => c.Item.Unpack<SchematicPin>()).Single();
        const long PowerValueFromCentreNm = 6_350_000;
        bool vertical = Math.Abs(probePin.Position.YNm) >= Math.Abs(probePin.Position.XNm);
        Assert.AreNotEqual(0L, vertical ? probePin.Position.YNm : probePin.Position.XNm, "The probe's pin lies off its centre, on one side.");
        power.ValueField.Text.Position = new Vector2
        {
            XNm = powerAt.XNm - (vertical ? 0 : Math.Sign(probePin.Position.XNm) * PowerValueFromCentreNm),
            YNm = powerAt.YNm - (vertical ? Math.Sign(probePin.Position.YNm) * PowerValueFromCentreNm : 0)
        };
        var onBodyBox = Box(probed.ItemCandidates.Single(c => c.Id.Value == onBodyId.ToString("D")).Bounds);
        Assert.IsTrue(body2.Contains(onBodyBox) && Depth(body2, onBodyBox) > Tolerance / 1_000_000m,
            $"The seeded label must lie on U2's body ({body2}), not beside it: {onBodyBox}.");

        // Before the defects, KiCad paints U1's value alone as the layout left it, reading left to right, in a region 1.27 mm wider
        // than its measured glyphs at 20 pixels per millimetre; it is painted the same way again once it is upside down.
        const long PixelNm = 50_000, Margin = 1_270_000;
        static int LayerOf(SchematicViewSet rendered, string name) => rendered.AvailableLayers.Single(l => l.Name == name).Id;
        static Box2 GlyphRegion(PresentationBounds glyphs) => new()
        {
            Position = new() { XNm = glyphs.LeftNm - Margin, YNm = glyphs.TopNm - Margin },
            Size = new() { XNm = glyphs.RightNm - glyphs.LeftNm + 2 * Margin, YNm = glyphs.BottomNm - glyphs.TopNm + 2 * Margin }
        };
        static SchematicRenderView GlyphView(string key, Box2 region, int layer)
        {
            var view = new SchematicRenderView { Key = key, Region = region.Clone(),
                WidthPixels = (uint)Math.Clamp(Math.Ceiling(region.Size.XNm / (double)PixelNm), 64, 2048),
                HeightPixels = (uint)Math.Clamp(Math.Ceiling(region.Size.YNm / (double)PixelNm), 64, 2048) };
            view.NativeLayers.Add(layer);
            return view;
        }
        async Task<SchematicPreview> PaintAlone(string name, Box2 region, int layer)
        {
            var request = new RenderSchematicViews { Document = psu.Clone() };
            request.Views.Add(GlyphView(name, region, layer));
            var preview = (await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token)).Views.Single().Preview;
            Assert.IsTrue(preview.Png.Length > 0, name);
            await File.WriteAllBytesAsync(evidence("presentation-glyphs-" + name + ".png"), preview.Png.ToByteArray(), token);
            return preview;
        }
        var cleanPage = await RenderPsu("presentation-clean");
        var uprightValue = measured.Presentation.Objects.Single(o => o.PresentationRole == "field" && o.OwnerId?.Value == u1.ToString("D")
            && o.FieldName == "Value");
        Assert.IsTrue(uprightValue.Visible && uprightValue.GlyphBounds is not null, "U1's value is painted before the defects.");
        Assert.AreEqual(0d, uprightValue.ReadingAngleDegrees, 1e-9, "U1's value reads left to right before the defects.");
        var uprightPainting = await PaintAlone("u1-value-upright", GlyphRegion(Box(uprightValue.GlyphBounds)), LayerOf(cleanPage, "values"));

        var seed = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Presentation must-catch fixture on the PSU sheet" };
        void Update(Google.Protobuf.IMessage item) => seed.Operations.Add(new SchematicItemOperation { TargetDocument = psu.Clone(), Update = Any.Pack(item) });
        void Create(Google.Protobuf.IMessage item) => seed.Operations.Add(new SchematicItemOperation { TargetDocument = psu.Clone(), Create = Any.Pack(item) });
        Update(Moved(originals[u4], dx4, dy4)); Update(Moved(originals[j1], dx1, 0)); Update(upsideDown); Update(hidden); Update(unannotated);
        foreach (var (wire, label) in created) { Create(wire); Create(label); }
        Create(tap); Create(onBody); Create(global); Create(power);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(seed, token);
        var seeded = await State();
        var page = await RenderPsu("presentation-defects");
        var defects = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            { Document = psu.Clone(), ExpectedRevision = seeded.State.Revision.Clone(), IncludePresentation = true }, token);
        SchematicPresentationObject Fact(Guid id, string role) => defects.Presentation.Objects
            .Single(o => o.Id.Value == id.ToString("D") && o.PresentationRole == role);
        SchematicPresentationObject FieldFact(Guid owner, string name) => defects.Presentation.Objects
            .Single(o => o.PresentationRole == "field" && o.OwnerId?.Value == owner.ToString("D") && o.FieldName == name);
        PresentationBounds Glyphs(Guid owner, string name) => Box(FieldFact(owner, name).GlyphBounds);

        // The intentional patterns are exactly what they claim: the power symbol's one pin ends on U1's pin end and joins
        // "+3V3", the tap label sits on U3's pin end, and KiCad itself says the hidden '#PWR01' need not show.
        var powerPin = defects.Obstacles.Single(o => o.Id.Value == powerId.ToString("D")).SymbolPins.Pins.Single();
        Assert.AreEqual(u1Pin.Position, powerPin.Position, "The power symbol's pin ends exactly on U1's pin end.");
        Assert.AreEqual(SchematicPinPowerScope.SppsGlobal, powerPin.PowerScope);
        Assert.AreEqual("+3V3", powerPin.PowerNet);
        Assert.AreEqual(u3Pin.Position, defects.Obstacles.Single(o => o.Id.Value == tapId.ToString("D")).Anchor, "The tap label sits on U3's pin end.");
        var powerReference = FieldFact(powerId, "Reference");
        Assert.AreEqual("#PWR01", powerReference.Text);
        Assert.IsFalse(powerReference.Visible);
        Assert.IsFalse(powerReference.DesignatorRequired, "KiCad does not require a power symbol's reference to show.");
        Assert.IsTrue(FieldFact(r1, "Reference").DesignatorRequired);
        // The power symbol's value lies beside its body, not on it: no owner exemption is what keeps it quiet.
        var powerValueGlyphs = Glyphs(powerId, "Value");
        Assert.IsNull(Shared(powerValueGlyphs, Body(defects, powerId)),
            $"The power symbol's value {powerValueGlyphs} lies clear of its own body {Body(defects, powerId)}.");
        CollectionAssert.AreEqual(new[] { body2, body3, moved4, moved1 }, new[] { u2, u3, u4, j1 }.Select(id => Body(defects, id)).ToArray(),
            "KiCad measures the untouched and moved bodies exactly where they were put.");

        // KiCad's own pixels confirm the measured field glyphs: each field is painted alone (only its own native layer) in a
        // region 1.27 mm wider than its measured glyphs at 20 pixels per millimetre, and the painted ink must lie within two
        // pixels of the measured glyph box on every side.
        int Layer(string name) => LayerOf(page, name);
        var probes = new (string Name, Guid Owner, string Field, string Layer, double Reading)[]
        {
            ("u1-value-upside-down", u1, "Value", "values", 180),
            ("u4-value-left-to-right", u4, "Value", "values", 0),
            ("r1-reference-bottom-to-top", r1, "Reference", "references", 90),
            ("power-value-on-rotated-symbol", powerId, "Value", "values", 0),
            ("global-label-field", globalId, "Pixel note", "fields", 0)
        };
        var pixels = new List<object>();
        foreach (var batch in probes.Chunk(4))
        {
            var request = new RenderSchematicViews { Document = psu.Clone() };
            foreach (var p in batch)
            {
                var fact = FieldFact(p.Owner, p.Field);
                Assert.IsTrue(fact.Visible && fact.GlyphBounds is not null, p.Name + " is painted.");
                Assert.AreEqual(p.Reading, fact.ReadingAngleDegrees, 1e-9, p.Name + " is painted at the expected reading direction.");
                request.Views.Add(GlyphView(p.Name, GlyphRegion(Box(fact.GlyphBounds)), Layer(p.Layer)));
            }
            var rendered = await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token);
            foreach (var p in batch)
            {
                var image = rendered.Views.Single(v => v.Key == p.Name).Preview;
                byte[] png = image.Png.ToByteArray();
                await File.WriteAllBytesAsync(evidence("presentation-glyphs-" + p.Name + ".png"), png, token);
                var glyphs = Glyphs(p.Owner, p.Field);
                var (ink, pixel) = NativePresentationRaster.InkBounds(png, image.Viewport);
                long[] edges = [ink.LeftNm - glyphs.LeftNm, ink.TopNm - glyphs.TopNm, ink.RightNm - glyphs.RightNm, ink.BottomNm - glyphs.BottomNm];
                pixels.Add(new { probe = p.Name, text = FieldFact(p.Owner, p.Field).Text, glyphs, ink, pixelNm = pixel, edgeDifferencesNm = edges });
                Assert.IsTrue(edges.All(e => Math.Abs(e) <= 2 * pixel), $"{p.Name}: KiCad painted ink at {ink}, the check measured glyphs at "
                    + $"{glyphs}; the left, top, right and bottom edges differ by {string.Join(", ", edges)} nm, more than two pixels ({2 * pixel} nm).");
            }
        }
        // A box cannot tell text read left to right from the same text turned half round: both fill a box of the same size.
        // KiCad's pixels can (presentation re-review finding 5). U1's value painted upside down must have the shape of its upright
        // painting from before the defects turned half round, and clearly not its shape unturned. KiCad turns the value about its
        // anchor, so each painting covers its own measured glyphs; the shapes are compared on their ink boxes. This holds the
        // check's reading direction (SCH_FIELD::GetDrawRotation) to what the painter actually draws (sch_painter.cpp).
        var upsideDownPainting = await PaintAlone("u1-value-upside-down-reading", GlyphRegion(Glyphs(u1, "Value")), Layer("values"));
        Assert.AreEqual(uprightPainting.Viewport.PixelXDxNm, upsideDownPainting.Viewport.PixelXDxNm, 1e-9, "Both paintings have one scale.");
        Assert.AreEqual(uprightPainting.Viewport.PixelYDyNm, upsideDownPainting.Viewport.PixelYDyNm, 1e-9, "Both paintings have one scale.");
        var (turned, unturned) = NativePresentationRaster.HalfTurnAgreement(uprightPainting.Png.ToByteArray(), upsideDownPainting.Png.ToByteArray());
        pixels.Add(new { probe = "u1-value-reading-direction", text = FieldFact(u1, "Value").Text, turnedAgreement = turned, unturnedAgreement = unturned });
        Assert.IsTrue(turned >= 0.9, $"U1's value painted upside down must match the upright painting turned half round: agreement {turned:0.000}.");
        Assert.IsTrue(unturned <= 0.75 && turned - unturned >= 0.2,
            $"U1's value painted upside down must differ from the upright painting as it is: agreement {unturned:0.000} unturned, {turned:0.000} turned.");
        await File.WriteAllTextAsync(evidence("presentation-glyph-pixels.json"), JsonSerializer.Serialize(pixels), token);

        // The report names exactly the seeded defects on the PSU sheet, with measured values derived here from KiCad's own
        // measurement of the moved bodies and painted glyphs, and nothing on the untouched sheets. The label on U3's pin end,
        // the power symbol on U1's pin end and its hidden reference, and the global label with its field report nothing.
        var broken = await Check("defects", root, allSheets, seeded.State.Revision);
        foreach (var sheet in new[] { "ROOT", "CPU", "CPU_POWER" })
            Assert.IsFalse(On(broken.Findings, sheetPaths[sheet]).Any(), $"The untouched {sheet} sheet must report nothing: "
                + Describe(On(broken.Findings, sheetPaths[sheet])));
        var onPsu = On(broken.Findings, psuPath).ToArray();
        Guid Target(Guid owner, string name) => Field(broken.Targets, psuPath, owner, name);
        Assert.IsFalse(onPsu.Any(f => Ids(f).Contains(powerId) || Ids(f).Contains(Target(powerId, "Value"))),
            "The power symbol and its value beside it report nothing: " + Describe(onPsu.Where(f => Ids(f).Contains(powerId) || Ids(f).Contains(Target(powerId, "Value")))));
        var pageBox = Box(defects.Presentation.PageBounds);
        var wires = created.Select(c => Guid.Parse(c.Wire.Id.Value)).ToArray();
        var expectedPsu = new (string Rule, Guid[] Ids, decimal Measured, decimal Limit, string Unit)[]
        {
            ("page_overflow", [j1], -moved1.LeftNm / 1_000_000m, 0m, Mm),
            ("page_overflow", [Target(j1, "Reference")], Beyond(pageBox, Glyphs(j1, "Reference")), 0m, Mm),
            ("page_overflow", [Target(j1, "Value")], Beyond(pageBox, Glyphs(j1, "Value")), 0m, Mm),
            ("designator_not_visible", [Target(j1, "Reference")], Beyond(pageBox, Glyphs(j1, "Reference")), 0m, Mm),
            ("designator_not_visible", [Target(u2, "Reference")], 0m, 1m, Count),
            ("designator_unannotated", [Target(r1, "Reference")], 0m, 1m, Count),
            ("text_orientation", [Target(u1, "Value")], 180m, 90m, Degrees),
            ("body_overlap", [u3, u4], Depth(body3, moved4), 0.5m, Mm),
            ("body_overlap", [u2, u4], Depth(body2, moved4), 0.5m, Mm),
            ("field_overlap", [u2, Target(u4, "Value")], Depth(body2, Glyphs(u4, "Value")), 0.5m, Mm),
            ("field_overlap", [Target(u1, "Reference"), Target(u1, "Value")], Depth(Glyphs(u1, "Reference"), Glyphs(u1, "Value")), 0.5m, Mm),
            ("label_overlap", [u2, onBodyId], Depth(body2, Box(Fact(onBodyId, "label").Bounds)), 0.5m, Mm),
            ("excessive_crossings", wires, 3m, 2m, Count)
        };
        static string Row(string rule, IEnumerable<Guid> ids, decimal measured, decimal limit, string unit) =>
            rule + " [" + string.Join(",", ids.Select(id => id.ToString("D")).Order(StringComparer.Ordinal)) + "] " + Fixed(measured) + "/" + Fixed(limit) + " " + unit;
        CollectionAssert.AreEquivalent(expectedPsu.Select(e => Row(e.Rule, e.Ids, e.Measured, e.Limit, e.Unit)).ToArray(),
            onPsu.Select(f => Row(f.GetProperty("rule").GetString()!, Ids(f), Number(f, "measured")!.Value, Number(f, "limit")!.Value,
                f.GetProperty("unit").GetString()!)).ToArray(),
            "The PSU sheet reports exactly the seeded defects, once each, with their measured values: " + Describe(onPsu));
        // The two field overlaps the moved U4 and the upside-down U1 value cause, as KiCad measures them on this fixture.
        Assert.AreEqual(1.6038m, expectedPsu.Single(e => e.Rule == "field_overlap" && e.Ids.Contains(u2)).Measured);
        Assert.AreEqual(1.3116m, expectedPsu.Single(e => e.Rule == "field_overlap" && !e.Ids.Contains(u2)).Measured);
        var bodies = onPsu.Single(f => f.GetProperty("rule").GetString() == "body_overlap" && Ids(f).ToHashSet().SetEquals(new[] { u3, u4 }));
        Assert.AreEqual(overlap, Bounds(bodies), "The overlap is located exactly where KiCad draws both bodies.");
        Assert.AreEqual(moved1, Bounds(onPsu.Single(f => f.GetProperty("rule").GetString() == "page_overflow" && Ids(f).SequenceEqual(new[] { j1 }))));
        var crossed = onPsu.Single(f => f.GetProperty("rule").GetString() == "excessive_crossings");
        CollectionAssert.AreEqual(crossings.Select(x => Point(x, 182.88)).Select(p => new PresentationBounds(p.XNm, p.YNm, p.XNm, p.YNm)).ToArray(),
            crossed.GetProperty("locations").EnumerateArray().Select(l => Bounds(l)).ToArray());

        // Started from a child sheet, the check covers that sheet and the sheets below it only: from PSU it reports the same
        // PSU findings, from CPU it covers CPU and CPU_POWER and reports nothing.
        var fromPsu = await Check("defects-from-psu", psu, ["PSU"], seeded.State.Revision);
        CollectionAssert.AreEqual(Canonical(onPsu, broken.Targets), Canonical(On(fromPsu.Findings, psuPath), fromPsu.Targets),
            "A check started on the PSU sheet reports exactly the PSU findings of the whole-hierarchy check.");
        var fromCpu = await Check("defects-from-cpu", Instance("CPU"), ["CPU", "CPU_POWER"]);
        Assert.IsFalse(fromCpu.Findings.EnumerateArray().Any(f => f.GetProperty("sheetPath").GetString() != ""),
            "The CPU branch reports nothing: " + Describe(fromCpu.Findings.EnumerateArray()));
        // A revision the caller observed before the design changed, or a sheet instance KiCad has not loaded, is refused by name.
        string staleCode = await Refused("stale-revision", root, clean.State.State.Revision);
        Assert.AreEqual("presentation_revision_changed", staleCode);
        Assert.AreEqual("presentation_revision_changed", await Refused("stale-revision-displayed", root, clean.State.State.Revision, subsheets: false));
        var unloaded = psu.Clone(); unloaded.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        string unloadedCode = await Refused("unloaded-sheet", unloaded, null);
        Assert.AreEqual("presentation_sheet_not_loaded", unloadedCode);
        // A stale revision is refused as stale first, even when it names a sheet instance KiCad does not hold now, and in the
        // default mode too, where it names a sheet KiCad does not display (PSU is loaded, the root stays displayed) or holds.
        Assert.AreEqual("presentation_revision_changed", await Refused("stale-revision-unloaded-sheet", unloaded, clean.State.State.Revision));
        Assert.AreEqual("presentation_revision_changed", await Refused("stale-revision-undisplayed", psu, clean.State.State.Revision, subsheets: false));
        Assert.AreEqual("presentation_revision_changed", await Refused("stale-revision-unloaded-sheet-displayed", unloaded, clean.State.State.Revision,
            subsheets: false));
        // With the current revision, the default mode still checks only the displayed sheet: KiCad refuses to measure PSU there.
        Assert.AreEqual("native_status_3", await Refused("current-revision-undisplayed", psu, seeded.State.Revision, subsheets: false));
        // An overlap tolerance that would switch the overlap rules off (half the 1.27 mm grid or more) is refused in both modes.
        Assert.AreEqual("invalid_presentation", await Refused("tolerance-too-loose", root, null, overlapToleranceMm: 1000m));
        Assert.AreEqual("invalid_presentation", await Refused("tolerance-too-loose-displayed", root, null, subsheets: false,
            overlapToleranceMm: PresentationPolicy.OverlapToleranceLimitMm));

        // KiCad's own Undo shortcut removes the whole defect batch from the PSU sheet while the root stays displayed, and
        // every sheet instance reports nothing again.
        static SchematicHierarchyData WithoutCache(CheckedSchematicState state)
        {
            var data = state.Electrical.Hierarchy.Data.Clone();
            foreach (var screen in data.Instances) screen.CachedSymbols.Clear();
            return data;
        }
        bool Same(SchematicHierarchyData left, SchematicHierarchyData right) =>
            SchematicHierarchyDelta.Plan(left, right, token).Count == 0 && SchematicHierarchyDelta.Plan(right, left, token).Count == 0;
        await FocusedSchematicShortcut(client, root, processId, display, "z", token);
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (!Same(WithoutCache(await State()), WithoutCache(clean.State)))
            {
                try { await Task.Delay(250, wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new AssertFailedException("KiCad's Undo did not remove the presentation defects within 20 s.");
                }
            }
        }
        var undone = await Check("undone", root, allSheets);
        unexpected = undone.Findings.EnumerateArray().Where(f => f.GetProperty("sheetPath").GetString() != "").ToArray();
        Assert.IsEmpty(unexpected, "Undoing the defects must leave every sheet reporting nothing: " + Describe(unexpected));
        await RenderPsu("presentation-undone");

        // Reload the saved sheets so the editor ends exactly as the journey found it.
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var reverted = await State();
        Assert.IsFalse(reverted.State.NativeContentDirty);
        Assert.IsTrue(Same(reverted.Electrical.Hierarchy.Data, clean.State.Electrical.Hierarchy.Data), "Reloading restores the saved sheets.");

        return new
        {
            tool = "kicad_schematic_check_presentation", includeSubsheets = true, sheets = sheetNames,
            cleanRevision = new { clean.State.State.Revision.Epoch, clean.State.State.Revision.Sequence },
            defectsRevision = new { broken.State.State.Revision.Epoch, broken.State.State.Revision.Sequence },
            psuFindings = onPsu.Select(f => new { rule = f.GetProperty("rule").GetString(), objectIds = Ids(f), measured = Number(f, "measured"),
                limit = Number(f, "limit"), unit = f.GetProperty("unit").GetString() }).ToArray(),
            quietPatterns = new { labelOnU3PinEnd = tapId, powerSymbolOnU1PinEnd = powerId, hiddenPowerReference = "#PWR01", globalLabelWithField = globalId },
            glyphPixels = pixels, childStartPsuSameFindings = true, childStartCpuSheets = new[] { "/CPU/", "/CPU/CPU_POWER/" },
            staleRevisionCode = staleCode, unloadedSheetCode = unloadedCode,
            untouchedSheetsReportNothing = true, nativeUndoReportsNothing = true
        };
    }

    // ---- connection-aware initial placement (cn1-wiring-intent.md §10, ledger p1f9bc19460f72e2b) ----

    // The layout policy the PSU/CPU journeys use: the 1.27 mm placement grid, 2.54 mm between symbols, and the page
    // inset carried by the explicit usable regions instead.
    // Explicit fixture policy (§1.6.3 presentation): a 10 mm page inset and the bottom 50 mm kept for the title block, for every
    // sheet that receives a symbol, on the page KiCad measures at this revision. The root sheet receives none. Also returns each
    // fixture sheet's native path key.
    private static async Task<(List<SchematicLayoutRegion> Regions, Dictionary<string, PresentationBounds> Usable, Dictionary<string, string> SheetPaths)>
        PsuCpuRegions(NativeClient client, SchematicDesign seedBaseline, PsuCpuExpectedNative expected,
            KiCad.Automation.Protocol.DocumentRevision revision, CancellationToken token)
    {
        string PathKey(IEnumerable<Guid> ids) => string.Join('/', ids.Select(id => id.ToString("D")));
        var sheetPaths = expected.Sheets.ToDictionary(s => s.Key,
            s => PathKey(seedBaseline.SheetBindings.Single(b => b.SheetInstanceId == s.ModelSheetInstance).NativePath));
        var targetSheets = expected.Sheets.Where(s => expected.Symbols.Any(x => x.Sheet == s.Key)).ToArray();
        CollectionAssert.AreEquivalent(new[] { "PSU", "CPU", "CPU_POWER" }, targetSheets.Select(s => s.Key).ToArray());
        var regions = new List<SchematicLayoutRegion>();
        var usable = new Dictionary<string, PresentationBounds>(StringComparer.Ordinal);
        foreach (var sheet in targetSheets)
        {
            var screen = seedBaseline.Schematic.Instances.Single(s => SheetPathKey(s.Metadata.Document) == sheetPaths[sheet.Key]);
            var page = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = screen.Metadata.Document.Clone(), ExpectedRevision = revision.Clone() }, token);
            Assert.AreEqual(sheet.NativeScreen.ToString("D"), page.ScreenId.Value, sheet.Key);
            long inset = expected.Presentation.PageInsetMm * 1_000_000L, reserve = expected.Presentation.ReservedBottomMm * 1_000_000L;
            var bounds = new PresentationBounds(page.PageBounds.Position.XNm + inset, page.PageBounds.Position.YNm + inset,
                page.PageBounds.Position.XNm + page.PageBounds.Size.XNm - inset, page.PageBounds.Position.YNm + page.PageBounds.Size.YNm - reserve);
            regions.Add(new(sheet.NativeScreen, bounds, []));
            usable.Add(sheet.Key, bounds);
        }
        return (regions, usable, sheetPaths);
    }

    // This KiCad serves every piece CN-1 §8.3 requires and advertises connection realization, so plans use its own handshake.
    private static AutomationSession RequireRealizationAdvertised(AutomationSession session)
    {
        Assert.IsTrue(session.Capabilities.Contains(SchematicConnectedAddition.NativeCapability),
            "This KiCad must advertise " + SchematicConnectedAddition.NativeCapability + ": " + string.Join(",", session.Capabilities));
        return session;
    }

    private static readonly InitialLayoutPolicy PsuCpuLayoutPolicy = new(1_270_000, 2_540_000, 0);

    /// <summary>The stated distance, a nearest-connection bound: every new, coordinate-free symbol lands with at least one of its
    /// connected pins at most 50.8 mm (2 in) from the nearest already placed pin it connects to on its sheet. Its other connected
    /// pins are not bounded; milestone-1 realization joins each pin through its own short stub and label, and the journey records
    /// every pin's distance. The PSU sheet's usable width is 277 mm and the CPU sheet's 400 mm.</summary>
    internal const long NearestConnectionDistanceNm = 50_800_000;

    internal const string ConnectionTooLong = "connection_too_long";
    internal const string ExistingSymbolMoved = "existing_symbol_moved";
    internal const string ConnectionRoomBlocked = "connection_room_blocked";

    private static Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> LiveMeasure(NativeClient client) =>
        (request, token) => client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);

    private static Task<CheckedSchematicState> CaptureChecked(NativeClient client, DocumentSpecifier document, CancellationToken token) =>
        client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

    private static string SheetPathKey(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));

    // CN-1 §10 on the S1 seed: the frozen Complete stage, every occurrence coordinate-free, laid out as a connected addition
    // with this editor's own handshake (it advertises schematic.connection-realization.v1) and its own measurements.
    // No symbol overlaps another or an existing item or leaves its sheet's usable region, and every pin that needs a connection
    // keeps the room for its shortest stub (two connection-grid steps) and each label its net may put there, as KiCad measures
    // them at the proposed positions: no other symbol, item or other symbol's stub room is inside it. Must-catch: the same
    // symbols laid out by the ordinary, connection-unaware planner (the layout this journey creates next) leave pins without
    // that room. No symbol has an already placed partner, so no preferred anchor is reported. The milestone-1 realizer is run
    // on the proposal from the same live measurements and its outcome recorded here, not asserted: the NativeConnectedRealization
    // journey draws these connections in KiCad and asserts them. Nothing is saved, published or sent to the editor.
    private static async Task<object> RequirePsuCpuConnectedCompleteLayout(NativeClient client, PsuCpuNativeContext context,
        DesignRecoveryStore store, string path, string instanceId, IReadOnlyList<SchematicLayoutRegion> regions,
        Func<string, string> evidence, CancellationToken token)
    {
        var session = await client.HandshakeAsync(token);
        Assert.AreEqual(instanceId, session.InstanceId);
        var advertised = RequireRealizationAdvertised(session);
        var current = store.Read()!;
        var nativeBefore = await CaptureChecked(client, context.Root, token);
        byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets;
        var revision = current.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(PsuCpuFixture.Desired(context, PsuCpuStage.Complete), [])) };
        var live = LiveMeasure(client);
        const string Instructions = "Lay out the complete PSU/CPU circuit with room for every connection.";
        var proposal = await SchematicInitialLayoutPlanner.ProposeMeasuredAsync(revision, PsuCpuLayoutPolicy, regions, Instructions, live, advertised, token);
        Assert.IsTrue(proposal.CanPropose, "Complete-stage layout: " + string.Join("; ", proposal.Layout.Issues.Select(i => i.Code + " " + i.BodyId)));
        Assert.IsEmpty(proposal.PreferredAnchors, "Every symbol is new and coordinate-free, so none has an already placed partner.");
        Assert.IsEmpty(proposal.PowerAttachments, "The fixture declares no power symbols.");
        var again = await SchematicInitialLayoutPlanner.ProposeMeasuredAsync(revision, PsuCpuLayoutPolicy, regions, Instructions, live, advertised, token);
        Assert.AreEqual(proposal.DesiredXml, again.DesiredXml, "The same saved revision and editor revision give the same layout.");
        var aware = await ProposedRooms(proposal.DesiredDesign!);
        var awareIssues = aware.Sheets.SelectMany(s => ConnectedPlacementIssues(s, aware.Expectation)).ToArray();
        Assert.IsEmpty(awareIssues, "Connection-aware Complete layout: " + string.Join("; ", awareIssues.Select(i => i.Code + " " + string.Join(",", i.Symbols))));
        // Every one of the fixture's 41 connected pins, on PSU, CPU and CPU_POWER alike, has its stub room measured.
        RequireStubRooms(aware.Sheets, aware.Plan.Candidate!, aware.Plan.Connections!, [.. complete.SelectMany(n => n.Pins)], null, 41,
            "Connection-aware Complete layout");

        // Must-catch: the ordinary layout of the same symbols (their connections left out) keeps no such room.
        var ordinary = await SchematicInitialLayoutPlanner.ProposeMeasuredAsync(current.State, PsuCpuLayoutPolicy, regions, Instructions, live, token);
        Assert.IsTrue(ordinary.CanPropose);
        var unaware = await ProposedRooms(ordinary.DesiredDesign! with { Engineering = ordinary.DesiredDesign!.Engineering with
            { Circuit = ordinary.DesiredDesign.Engineering.Circuit with { Nets = complete } } });
        var unawareIssues = unaware.Sheets.SelectMany(s => ConnectedPlacementIssues(s, unaware.Expectation)).ToArray();
        CollectionAssert.Contains(unawareIssues.Select(i => i.Code).Distinct().ToArray(), ConnectionRoomBlocked,
            "The connection-unaware layout must leave some pin without room for its stub and label.");

        // The milestone-1 realizer on the connection-aware proposal, measuring this editor, recorded as it answers.
        var checkpoint = await CaptureChecked(client, context.Root, token);
        Assert.AreEqual(nativeBefore, checkpoint, "Layout measurement must not change the native document.");
        object realizer;
        try
        {
            var realization = await SchematicConnectionRealizer.RealizeAsync(aware.Plan.Connections!, aware.Plan.Candidate!, checkpoint, live,
                SchematicConnectionPolicy.FromSnapshot(checkpoint.Electrical.Hierarchy.Data), token);
            realizer = new { refused = false, generated = realization.Generated.GroupBy(g => g.Role.ToString()).ToDictionary(g => g.Key, g => g.Count()) };
        }
        catch (AutomationException error) { realizer = new { refused = true, error.Code, error.Message }; }
        Assert.AreEqual(nativeBefore, await CaptureChecked(client, context.Root, token), "Planning and realizing connections read-only must not change the native document.");
        Assert.AreEqual(current.RevisionToken, store.Read()!.RevisionToken, "Layout must not write the recovery record.");
        CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Layout must not publish XML.");
        var summary = new
        {
            placedSymbols = proposal.Layout.Placements!.Count, measuredRooms = aware.Sheets.Sum(s => s.Rooms.Count), issues = awareIssues.Length,
            unawareBlockedRooms = unawareIssues.Count(i => i.Code == ConnectionRoomBlocked),
            unawareIssues = unawareIssues.Select(i => i.Code + " " + string.Join(",", i.Symbols)).ToArray(), realizerObservation = realizer
        };
        await File.WriteAllTextAsync(evidence("complete-layout.json"), JsonSerializer.Serialize(new { summary, layout = proposal.Layout,
            ordinaryLayout = ordinary.Layout }), token);
        return summary;

        // The Complete nets over a laid-out design, planned as a connected addition, and its created symbols measured by KiCad
        // at their positions with the room each connected pin needs.
        async Task<(SchematicSynchronizationPlan Plan, List<ConnectedSheetGeometry> Sheets, ConnectedPlacementExpectation Expectation)> ProposedRooms(SchematicDesign placed)
        {
            var plan = SchematicSynchronizationPlanner.Plan(revision with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(placed, [])) },
                advertised, token);
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
            SchematicSynchronizationPlanTests.RequirePsuCpuIntent(intent, plan.Candidate!, created: true);
            var policy = SchematicConnectionPolicy.FromSnapshot(nativeBefore.Electrical.Hierarchy.Data);
            var created = intent.CreatedSymbolIds.ToHashSet();
            var expectation = new ConnectedPlacementExpectation(created, created, intent, policy.GridNm * SchematicConnectionPolicy.StubMultiples[0]);
            var sheets = new List<ConnectedSheetGeometry>();
            foreach (var region in regions)
                foreach (var screen in plan.Candidate!.Schematic.Instances.Where(s => Guid.Parse(s.Metadata.ScreenId.Value) == region.ScreenId))
                {
                    var sheet = await MeasureProposedSheet(client, screen, created, nativeBefore.State.Revision, region.UsableBounds, token);
                    await MeasureStubRoom(client, sheet, expectation, policy, nativeBefore.State.Revision, token);
                    sheets.Add(sheet);
                }
            return (plan, sheets, expectation);
        }
    }

    // A sheet of a planned design as KiCad measures it at the recorded revision: the planned new symbols as detached candidates
    // at their planned positions and every existing item as it is, with every symbol's pins, and each existing symbol's
    // position as KiCad draws it (Before) and as the planned design places it (After).
    private static async Task<ConnectedSheetGeometry> MeasureProposedSheet(NativeClient client, SchematicScreenData screen,
        IReadOnlySet<Guid> created, KiCad.Automation.Protocol.DocumentRevision revision, PresentationBounds usable, CancellationToken token)
    {
        var candidates = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Where(s => created.Contains(Guid.Parse(s.Id.Value))).OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
        var request = new MeasureSchematicPlacement { Document = screen.Metadata.Document.Clone(), ExpectedRevision = revision.Clone() };
        request.Candidates.Add(candidates.Select(s => s.Clone()));
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
        Assert.IsTrue(measured.PinGeometryAvailable);
        Assert.HasCount(candidates.Length, measured.Candidates);
        var bodies = new Dictionary<Guid, PresentationBounds>();
        // Existing symbols as KiCad draws them, and as the planned design places them.
        var before = new Dictionary<Guid, PresentationPoint>();
        var after = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Where(s => !created.Contains(Guid.Parse(s.Id.Value))).ToDictionary(s => Guid.Parse(s.Id.Value), s => new PresentationPoint(s.Position.XNm, s.Position.YNm));
        var pins = new Dictionary<Guid, PresentationPoint>();
        var anchors = new Dictionary<Guid, SchematicPinAnchor>();
        var owner = new Dictionary<Guid, Guid>();
        var items = new List<(Guid, PresentationBounds)>();
        foreach (var symbol in measured.Candidates.Concat(measured.Obstacles))
        {
            Guid id = Guid.Parse(symbol.Id.Value);
            if (symbol.SymbolPins is null) { items.Add((id, Presentation(symbol.Bounds))); continue; }
            Assert.IsTrue(symbol.SymbolPins.Complete, "Every symbol reports complete pins.");
            bodies[id] = Presentation(symbol.Bounds);
            if (!created.Contains(id)) before[id] = new(symbol.Anchor.XNm, symbol.Anchor.YNm);
            foreach (var pin in symbol.SymbolPins.Pins)
            {
                Guid pinId = Guid.Parse(pin.Id.Value);
                pins[pinId] = new(pin.Position.XNm, pin.Position.YNm); anchors[pinId] = pin; owner[pinId] = id;
            }
        }
        // Placement measurement gives one envelope per symbol (body, pins and visible fields); it serves for both checks.
        return new(SheetPathKey(screen.Metadata.Document), screen.Metadata.Document.Clone(), usable, bodies, new(bodies), before, after,
            pins, anchors, owner, items, []);
    }

    // CN-1 §10 on the created Components stage: the fixture's Complete nets plus I2C pull-up resistors, R2 (PSU_SCL to RAIL_B)
    // coordinate-free and R3 (PSU_SDA to RAIL_B) placed explicitly and locked on PSU, and R4 (MEM_SCL to RAIL_B) coordinate-free
    // on CPU. The public layout tool over STDIO proposes the connected addition with this editor's handshake, which advertises
    // connection realization, exactly as the same planner does in process on this editor's measurements: it aims R2 and R4 at the
    // placed pins they connect to. KiCad then measures the proposal: existing symbols unmoved, R3 exactly where the XML put it,
    // R2 and R4 each with its nearest connection within NearestConnectionDistanceNm (at least one connected pin that close to a
    // placed pin it connects to; every pin's distance is recorded, and a pull-up's other pin may be farther), no symbol
    // overlapping another, every symbol inside its usable region, and the room for each new pin's shortest stub and its label
    // clear of every other symbol, item and other symbol's stub room. The checks are shown to catch an overlap, a symbol off the
    // page, a moved existing symbol, a distant symbol and a blocked stub, and to pass the real layout unchanged. Applying the
    // addition is the NativeConnectedRealization journey's; here the design file and recovery record are restored and KiCad is
    // never changed.
    private static async Task<object> RequirePsuCpuConnectedAddition(NativeClient client, PsuCpuNativeContext context, DesignRecoveryStore store,
        string path, string instanceId, StdioMcpFixture host, PsuCpuExpectedNative expected, IReadOnlyDictionary<string, string> sheetPaths,
        IReadOnlyList<SchematicLayoutRegion> allRegions, Func<string, string> evidence, CancellationToken token)
    {
        var session = await client.HandshakeAsync(token);
        var advertised = RequireRealizationAdvertised(session);
        var current = store.Read()!;
        Assert.IsFalse(current.State.HasPendingWork);
        byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
        var baseline = current.State.Baseline;
        var pinned = new SymbolPlacement(215.9m, 76.2m, 0, false, false, true);
        var (connected, added) = WithPullUps(baseline, expected, pinned);
        byte[] connectedBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(connected, []));
        await File.WriteAllBytesAsync(path, connectedBytes, token);
        var saved = store.Save(current.State with { DesiredFileBytes = connectedBytes }, current.RevisionToken);
        var screenOf = expected.Sheets.ToDictionary(s => s.Key, s => s.NativeScreen);
        var regions = allRegions.Where(r => r.ScreenId == screenOf["PSU"] || r.ScreenId == screenOf["CPU"]).ToArray();
        Assert.HasCount(2, regions);
        var nativeBefore = await CaptureChecked(client, context.Root, token);
        const string Instructions = "Place the I2C pull-ups next to the bus pins they serve.";

        // The public tool over STDIO proposes the connected addition from its own handshake and measurements, and writes nothing.
        var publicLayout = await host.Tool("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = saved.RevisionToken, gridNm = PsuCpuLayoutPolicy.GridNm, clearanceNm = PsuCpuLayoutPolicy.ClearanceNm,
            pageInsetNm = PsuCpuLayoutPolicy.PageInsetNm, regions, userInstructions = Instructions });
        await File.WriteAllTextAsync(evidence("connected-layout-public.json"), RetainedToolEvidence(publicLayout), token);
        RequireToolSuccess(publicLayout);
        var published = publicLayout.GetProperty("structuredContent");
        Assert.IsTrue(published.GetProperty("canPropose").GetBoolean(), publicLayout.GetRawText());
        Assert.AreEqual(nativeBefore, await CaptureChecked(client, context.Root, token));
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(connectedBytes, await File.ReadAllBytesAsync(path, token));

        var live = LiveMeasure(client);
        var proposal = await SchematicInitialLayoutPlanner.ProposeMeasuredAsync(saved.State, PsuCpuLayoutPolicy, regions, Instructions, live, advertised, token);
        Assert.IsTrue(proposal.CanPropose, "Connected addition layout: " + string.Join("; ", proposal.Layout.Issues.Select(i => i.Code + " " + i.BodyId)));
        Assert.AreEqual(proposal.DesiredXml, published.GetProperty("desiredXml").GetString(),
            "The public tool proposes exactly the planner's connected layout for this editor.");
        var again = await SchematicInitialLayoutPlanner.ProposeMeasuredAsync(saved.State, PsuCpuLayoutPolicy, regions, Instructions, live, advertised, token);
        Assert.AreEqual(proposal.DesiredXml, again.DesiredXml, "The same saved revision and editor revision give the same layout.");
        var design = proposal.DesiredDesign!;
        var oldSymbols = baseline.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        foreach (var symbol in design.Engineering.Circuit.Symbols)
            if (oldSymbols.TryGetValue(symbol.Id, out var old)) Assert.AreEqual(old, symbol, "Existing occurrences keep their XML exactly.");
        Assert.AreEqual(pinned, design.Engineering.Circuit.Symbols.Single(s => s.Id == added["R3"].Occurrence).Placement,
            "The explicitly placed, locked R3 keeps its position.");
        foreach (var reference in new[] { "R2", "R4" })
        {
            var placement = design.Engineering.Circuit.Symbols.Single(s => s.Id == added[reference].Occurrence).Placement!;
            Assert.AreEqual(0, Coordinates.MillimetersToNanometers(placement.XMillimeters) % PsuCpuLayoutPolicy.GridNm, reference);
            Assert.AreEqual(0, Coordinates.MillimetersToNanometers(placement.YMillimeters) % PsuCpuLayoutPolicy.GridNm, reference);
        }
        // The proposal as synchronization would plan it: R2 and R4 were aimed at exactly the placed pins they connect to on
        // their own sheet (R3, placed explicitly, among them).
        var plan = SchematicSynchronizationPlanner.Plan(saved.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(proposal.DesiredXml!) }, advertised, token);
        var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
        var candidate = plan.Candidate!;
        var refs = candidate.Engineering.Circuit.Components.ToDictionary(c => c.Reference, c => c.Id);
        Guid PinId((string Reference, string Number) pin) => SchematicConnectionIntentBuilderTests.Keys(candidate, (refs[pin.Reference], pin.Number)).Single().PlacedPinId;
        var bodies = candidate.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
        var expectedPartners = new Dictionary<string, (string, string)[]>
        {
            ["R2"] = [("U3", "6"), ("U4", "5"), ("U2", "1"), ("U2", "4"), ("U3", "8"), ("R3", "2")],
            ["R4"] = [("U5", "161"), ("U6", "6"), ("U6", "8")]
        };
        CollectionAssert.AreEquivalent(new[] { bodies[added["R2"].Occurrence], bodies[added["R4"].Occurrence] },
            proposal.PreferredAnchors.Select(a => a.BodyId).ToArray(), "Exactly the coordinate-free pull-ups are aimed at their partners.");
        foreach (var (reference, partners) in expectedPartners)
            CollectionAssert.AreEquivalent(partners.Select(PinId).ToArray(),
                proposal.PreferredAnchors.Single(a => a.BodyId == bodies[added[reference].Occurrence]).PartnerPins.ToArray(), reference);
        await File.WriteAllTextAsync(evidence("connected-layout.json"), JsonSerializer.Serialize(new
        {
            preferredAnchors = proposal.PreferredAnchors, powerAttachments = proposal.PowerAttachments, layout = proposal.Layout,
            placements = added.ToDictionary(a => a.Key, a => design.Engineering.Circuit.Symbols.Single(s => s.Id == a.Value.Occurrence).Placement)
        }), token);

        // KiCad measures the proposal: the new symbols at their proposed positions beside every existing item, and the room
        // each new pin's shortest stub and label need there.
        var policy = SchematicConnectionPolicy.FromSnapshot(nativeBefore.Electrical.Hierarchy.Data);
        var created = intent.CreatedSymbolIds.ToHashSet();
        CollectionAssert.AreEquivalent(added.Values.Select(a => bodies[a.Occurrence]).ToArray(), created.ToArray());
        var free = new[] { "R2", "R4" }.Select(r => bodies[added[r].Occurrence]).ToHashSet();
        var expectation = new ConnectedPlacementExpectation(created, free, intent, policy.GridNm * SchematicConnectionPolicy.StubMultiples[0]);
        var sheets = new List<ConnectedSheetGeometry>();
        foreach (var key in new[] { "PSU", "CPU" })
        {
            var screen = candidate.Schematic.Instances.Single(s => SheetPathKey(s.Metadata.Document) == sheetPaths[key]);
            var sheet = await MeasureProposedSheet(client, screen, created, nativeBefore.State.Revision,
                allRegions.Single(r => r.ScreenId == screenOf[key]).UsableBounds, token) with { Key = key };
            await MeasureStubRoom(client, sheet, expectation, policy, nativeBefore.State.Revision, token);
            sheets.Add(sheet);
        }
        var r3 = sheets.Single(s => s.Key == "PSU").Bodies.ContainsKey(bodies[added["R3"].Occurrence]);
        Assert.IsTrue(r3, "R3 is measured on PSU.");
        // The fixture's Complete-net pins on PSU and CPU and the six pull-up pins: 45 rooms. The fixture's other two connected pins
        // are the processor's unit-4 power pins on CPU_POWER, which the addition does not lay out.
        var pullUpPins = new[] { "R2", "R3", "R4" }.SelectMany(r => new[] { "1", "2" }.Select(n => new PinEndpoint(added[r].Component, n))).ToArray();
        PinEndpoint[] connectedPins = [.. PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets.SelectMany(n => n.Pins), .. pullUpPins];
        var addedSheets = new HashSet<string>(StringComparer.Ordinal) { sheetPaths["PSU"], sheetPaths["CPU"] };
        RequireStubRooms(sheets, candidate, intent, connectedPins, addedSheets, 45, "Connected addition");
        var elsewhere = SchematicConnectionIntentBuilderTests.Keys(candidate, [.. connectedPins.Select(p => (p.ComponentId, p.Pin))])
            .Where(k => !addedSheets.Contains(k.SheetPathKey)).ToArray();
        Assert.HasCount(2, elsewhere, "Connected addition: two connected fixture pins lie outside PSU and CPU.");
        Assert.IsTrue(elsewhere.All(k => k.SheetPathKey == sheetPaths["CPU_POWER"]), "Connected addition: both are on CPU_POWER.");
        var roomed = sheets.SelectMany(s => s.Rooms).Select(r => r.Pin).ToHashSet();
        foreach (var (reference, number) in new[] { ("R2", "1"), ("R2", "2"), ("R3", "1"), ("R3", "2"), ("R4", "1"), ("R4", "2") })
            Assert.IsTrue(roomed.Contains(PinId((reference, number))), "Connected addition: pull-up pin " + reference + "." + number + " has its stub room measured.");
        var issues = sheets.SelectMany(s => ConnectedPlacementIssues(s, expectation)).ToArray();
        Assert.IsEmpty(issues, string.Join("; ", issues.Select(i => i.Code + " " + string.Join(",", i.Symbols))));

        // Must-catch: each defect class is found in otherwise real geometry, and only there.
        var psu = sheets.Single(s => s.Key == "PSU");
        Guid r2 = bodies[added["R2"].Occurrence];
        Guid neighbour = psu.Before.Keys.Order().First();
        var caught = new Dictionary<string, string[]>
        {
            ["overlap"] = Codes(psu with { Bodies = Moved(psu.Bodies, r2, Between(Centre(psu.Bodies[r2]), Centre(psu.Bodies[neighbour]))) }),
            ["offPage"] = Codes(psu with { Bodies = Moved(psu.Bodies, r2, new(psu.Usable.RightNm - psu.Bodies[r2].LeftNm, 0)),
                Fields = Moved(psu.Fields, r2, new(psu.Usable.RightNm - psu.Bodies[r2].LeftNm, 0)) }),
            ["movedExisting"] = Codes(psu with { After = psu.After.ToDictionary(p => p.Key, p => p.Key == neighbour ? p.Value with { XNm = p.Value.XNm + 1_270_000 } : p.Value) }),
            // R2's pins moved 110 mm down, to the bottom of the sheet, far from every pin they connect to (R3 included).
            ["distant"] = Codes(psu with { Pins = psu.Pins.ToDictionary(p => p.Key, p => psu.Owner[p.Key] == r2 ? new PresentationPoint(p.Value.XNm, p.Value.YNm + 110_000_000) : p.Value) }),
            ["blockedStub"] = Codes(psu with { Items = [.. psu.Items, (Guid.NewGuid(), psu.Rooms.First(r => r.Owner == r2).Label)] }),
            ["unchanged"] = Codes(psu)
        };
        CollectionAssert.AreEqual(new[] { NativePresentationChecks.SymbolBodiesOverlap }, caught["overlap"]);
        CollectionAssert.AreEqual(new[] { NativePresentationChecks.SymbolOutsideUsableRegion }, caught["offPage"]);
        CollectionAssert.AreEqual(new[] { ExistingSymbolMoved }, caught["movedExisting"]);
        CollectionAssert.AreEqual(new[] { ConnectionTooLong }, caught["distant"]);
        CollectionAssert.AreEqual(new[] { ConnectionRoomBlocked }, caught["blockedStub"]);
        Assert.IsEmpty(caught["unchanged"], "False-positive guard: the real layout passes every check.");
        string[] Codes(ConnectedSheetGeometry sheet) => [.. ConnectedPlacementIssues(sheet, expectation).Select(i => i.Code).Distinct().Order(StringComparer.Ordinal)];

        // The milestone-1 realizer on the proposal, measuring this editor, recorded as it answers (not asserted): the Components
        // stage it would also wire was laid out before its connections existed.
        object realizer;
        try
        {
            var realization = await SchematicConnectionRealizer.RealizeAsync(intent, candidate, nativeBefore, live, policy, token);
            realizer = new { refused = false, generated = realization.Generated.Count };
        }
        catch (AutomationException error) { realizer = new { refused = true, error.Code, error.Message }; }

        // Nothing reached KiCad; the design file and the recovery record's desired XML are restored for the rest of the journey.
        Assert.AreEqual(nativeBefore, await CaptureChecked(client, context.Root, token), "Connected placement must not change the native document.");
        await File.WriteAllBytesAsync(path, fileBefore, token);
        var restored = store.Save(store.Read()!.State with { DesiredFileBytes = current.State.DesiredFileBytes }, store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(current.State.DesiredFileBytes, restored.State.DesiredFileBytes);
        // Every connected pin's distance is recorded; only each symbol's nearest connection is bounded (NearestConnectionDistanceNm).
        var distances = sheets.SelectMany(s => PartnerDistances(s, expectation)).ToArray();
        var names = candidate.Engineering.Circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
        var summary = new
        {
            publicToolMatchesPlanner = true, deterministic = true, preferredAnchors = proposal.PreferredAnchors.Count,
            pinnedR3Preserved = true, existingSymbolsUnmoved = true, nearestConnectionBoundNm = NearestConnectionDistanceNm,
            nearestConnectionNm = distances.GroupBy(d => names[d.Endpoint.ComponentId]).ToDictionary(g => g.Key, g => g.Min(d => d.DistanceNm)),
            longestConnectionNm = distances.GroupBy(d => names[d.Endpoint.ComponentId]).ToDictionary(g => g.Key, g => g.Max(d => d.DistanceNm)),
            connections = distances.Select(d => new { d.Sheet, pin = names[d.Endpoint.ComponentId] + "." + d.Endpoint.Pin, d.Net, d.DistanceNm }),
            measuredRooms = sheets.Sum(s => s.Rooms.Count), mustCatch = caught, realizerObservation = realizer
        };
        await File.WriteAllTextAsync(evidence("connected-addition.json"), JsonSerializer.Serialize(summary), token);
        return summary;
    }

    /// <summary>The Components-stage design plus the pull-up resistors R2, R3 and R4 (Device:R, value 4k7) and the fixture's
    /// Complete nets extended with their pins. R3 carries <paramref name="pinned"/>; R2 and R4 are coordinate-free.</summary>
    private static (SchematicDesign Design, Dictionary<string, (Guid Component, Guid Occurrence)> Added) WithPullUps(SchematicDesign baseline,
        PsuCpuExpectedNative expected, SymbolPlacement pinned)
    {
        var circuit = baseline.Engineering.Circuit;
        var part = circuit.Parts.Single(p => p.Name == "R");
        var sheet = expected.Sheets.ToDictionary(s => s.Key);
        // Journey-local identities in the fixture's scheme, under a kind the frozen fixture does not use.
        (string Reference, string Sheet, SymbolPlacement? Placement)[] pullUps = [("R2", "PSU", null), ("R3", "PSU", pinned), ("R4", "CPU", null)];
        var added = new Dictionary<string, (Guid Component, Guid Occurrence)>();
        var definitions = new List<(string Sheet, ComponentDefinition Definition)>();
        var components = new List<ComponentInstance>();
        var symbols = new List<SymbolOccurrence>();
        for (int i = 0; i < pullUps.Length; i++)
        {
            var (reference, key, placement) = pullUps[i];
            var definition = new ComponentDefinition(PsuCpuIds.Id(0xa2, 0x10 + i), part.Id, "4k7");
            var component = new ComponentInstance(PsuCpuIds.Id(0xa2, 0x20 + i), definition.Id, sheet[key].ModelSheetInstance, reference);
            var occurrence = new SymbolOccurrence(PsuCpuIds.Id(0xa2, 0x30 + i), component.Id, 1, placement);
            definitions.Add((key, definition)); components.Add(component); symbols.Add(occurrence);
            added.Add(reference, (component.Id, occurrence.Id));
        }
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit.Nets;
        PinEndpoint Pin(string reference, string number) => new(added[reference].Component, number);
        var extra = new Dictionary<string, PinEndpoint[]>
        {
            ["PSU_SCL"] = [Pin("R2", "1")], ["PSU_SDA"] = [Pin("R3", "1")], ["MEM_SCL"] = [Pin("R4", "1")],
            ["RAIL_B"] = [Pin("R2", "2"), Pin("R3", "2"), Pin("R4", "2")]
        };
        var nets = complete.Select(n => extra.TryGetValue(n.Name, out var pins) ? n with { Pins = [.. n.Pins, .. pins] } : n).ToArray();
        return (baseline with { Engineering = baseline.Engineering with { Circuit = circuit with
        {
            Sheets = circuit.Sheets.Select(s => s with { Components = [.. s.Components, .. definitions
                .Where(d => sheet[d.Sheet].Definition == s.Id).Select(d => d.Definition)] }).ToArray(),
            Components = [.. circuit.Components, .. components], Symbols = [.. circuit.Symbols, .. symbols], Nets = nets
        } } }, added);
    }

    /// <summary>One sheet of a proposed layout as KiCad measures it: every symbol's envelope (body, pins and visible fields),
    /// every existing symbol's position as drawn and as planned, every measured pin with its owner, every non-symbol item's
    /// measured bounds, and the room each new pin's shortest stub and label need there.</summary>
    internal sealed record ConnectedSheetGeometry(string Key, DocumentSpecifier Document, PresentationBounds Usable,
        Dictionary<Guid, PresentationBounds> Bodies, Dictionary<Guid, PresentationBounds> Fields,
        Dictionary<Guid, PresentationPoint> Before, Dictionary<Guid, PresentationPoint> After,
        Dictionary<Guid, PresentationPoint> Pins, Dictionary<Guid, SchematicPinAnchor> Anchors, Dictionary<Guid, Guid> Owner,
        List<(Guid Id, PresentationBounds Bounds)> Items, List<ConnectedStubRoom> Rooms)
    {
        public string PathKey => SheetPathKey(Document);
    }

    /// <summary>The shortest stub of pin <paramref name="Pin"/> on symbol <paramref name="Owner"/> and the union of the
    /// label envelopes KiCad measures at its end.</summary>
    internal sealed record ConnectedStubRoom(Guid Owner, Guid Pin, PresentationBounds Stub, PresentationBounds Label);

    internal sealed record ConnectedPlacementExpectation(IReadOnlySet<Guid> NewSymbols, IReadOnlySet<Guid> FreeSymbols,
        SchematicConnectionIntent Intent, long StubLengthNm);

    // The room the shortest stub of each pin that needs one takes: from its pin away from its body, ending in every label kind
    // its net may put there, each measured by KiCad at that very spot (never inserted).
    private static async Task MeasureStubRoom(NativeClient client, ConnectedSheetGeometry sheet, ConnectedPlacementExpectation expectation,
        SchematicConnectionPolicy policy, KiCad.Automation.Protocol.DocumentRevision revision, CancellationToken token)
    {
        var islands = expectation.Intent.Screens.SelectMany(s => s.Islands).Where(i => i.SheetPathKey == sheet.PathKey).ToArray();
        var request = new MeasureSchematicPlacement { Document = sheet.Document.Clone(), ExpectedRevision = revision.Clone() };
        var pending = new List<(ConnectionPlacedPin Pin, PresentationBounds Stub, int First, int Count)>();
        foreach (var island in islands)
            foreach (var member in island.Members.Where(m => m.RequiresStub))
            {
                var anchor = sheet.Anchors[member.Pin.PlacedPinId];
                var outward = SchematicConnectionGeometry.Outward(anchor);
                var end = SchematicConnectionGeometry.StubEnd(anchor.Position, outward, expectation.StubLengthNm);
                var kinds = island.Scope == ConnectionScope.Global ? new[] { ConnectionLabelKind.Global }
                    : island.UplinkSheetSymbolId is null ? [ConnectionLabelKind.Local] : [ConnectionLabelKind.Local, ConnectionLabelKind.Hierarchical];
                pending.Add((member.Pin, new(Math.Min(anchor.Position.XNm, end.XNm), Math.Min(anchor.Position.YNm, end.YNm),
                    Math.Max(anchor.Position.XNm, end.XNm), Math.Max(anchor.Position.YNm, end.YNm)), request.ItemCandidates.Count, kinds.Length));
                foreach (var kind in kinds)
                    request.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(kind, Guid.NewGuid(), end, island.LabelText,
                        SchematicConnectionGeometry.Spin(outward), policy)));
            }
        if (pending.Count == 0) return;
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
        Assert.HasCount(request.ItemCandidates.Count, measured.ItemCandidates);
        foreach (var (pin, stub, first, count) in pending)
        {
            var label = measured.ItemCandidates.Skip(first).Take(count).Select(c => Presentation(c.Bounds))
                .Aggregate((a, b) => new PresentationBounds(Math.Min(a.LeftNm, b.LeftNm), Math.Min(a.TopNm, b.TopNm), Math.Max(a.RightNm, b.RightNm), Math.Max(a.BottomNm, b.BottomNm)));
            sheet.Rooms.Add(new(pin.SymbolId, pin.PlacedPinId, stub, label));
        }
    }

    /// <summary>The measured stub rooms against a list built without the connection intent (placement re-review finding 2): the
    /// placed pins of exactly <paramref name="pins"/> (the fixture's Complete nets, plus the pull-ups' pins in the addition), found
    /// in the planned design by component and pin number, on <paramref name="onlySheets"/> when given. There must be exactly
    /// <paramref name="count"/> of them; every sheet holding one must have been measured; each must have exactly one measured
    /// room and no other pin a room. The connection intent must then agree: the pins it gives a stub on the measured sheets are
    /// exactly these.</summary>
    private static void RequireStubRooms(IReadOnlyList<ConnectedSheetGeometry> sheets, SchematicDesign candidate, SchematicConnectionIntent intent,
        IReadOnlyCollection<PinEndpoint> pins, IReadOnlySet<string>? onlySheets, int count, string context)
    {
        var all = SchematicConnectionIntentBuilderTests.Keys(candidate, [.. pins.Select(p => (p.ComponentId, p.Pin))]);
        var expected = all.Where(k => onlySheets is null || onlySheets.Contains(k.SheetPathKey)).Select(k => (Sheet: k.SheetPathKey, Pin: k.PlacedPinId)).ToArray();
        Assert.HasCount(count, expected, context + ": the fixture pins that need a connection on the checked sheets.");
        Assert.HasCount(count, expected.Distinct().ToArray(), context + ": each fixture pin is one placed pin.");
        var measuredSheets = sheets.Select(s => s.PathKey).ToHashSet(StringComparer.Ordinal);
        foreach (var sheet in expected.Select(e => e.Sheet).Distinct(StringComparer.Ordinal))
            Assert.IsTrue(measuredSheets.Contains(sheet), context + ": sheet " + sheet + ", which holds pins that need a connection, was measured.");
        var measured = sheets.SelectMany(s => s.Rooms.Select(r => (Sheet: s.PathKey, r.Pin))).ToArray();
        Assert.HasCount(count, measured, context + ": one measured room per pin that needs a connection.");
        CollectionAssert.AreEquivalent(expected, measured, context + ": the measured rooms belong to exactly the fixture pins that need a connection.");
        var stubbed = intent.Screens.SelectMany(s => s.Islands).Where(i => measuredSheets.Contains(i.SheetPathKey))
            .SelectMany(i => i.Members.Where(m => m.RequiresStub).Select(m => (Sheet: i.SheetPathKey, Pin: m.Pin.PlacedPinId))).ToArray();
        CollectionAssert.AreEquivalent(expected, stubbed, context + ": the connection intent gives exactly these pins a stub.");
    }

    internal sealed record ConnectedPlacementIssue(string Code, IReadOnlyList<Guid> Symbols);

    /// <summary>Everything wrong with a connected addition's layout on one sheet: a symbol outside the usable region or two
    /// symbol bodies overlapping (<see cref="NativePresentationChecks"/>), an existing symbol that moved, a connected pin of a
    /// new coordinate-free symbol none of whose connected pins lies within <see cref="NearestConnectionDistanceNm"/> of a placed
    /// pin it connects to,
    /// a new pin whose shortest stub or label meets another symbol, an item or another symbol's stub room, and an existing pin
    /// whose stub or label meets a new symbol or a new symbol's stub room.</summary>
    internal static IReadOnlyList<ConnectedPlacementIssue> ConnectedPlacementIssues(ConnectedSheetGeometry sheet, ConnectedPlacementExpectation expectation)
    {
        var issues = new List<ConnectedPlacementIssue>();
        var ids = sheet.Bodies.Keys.Order().ToArray();
        issues.AddRange(NativePresentationChecks.SymbolPlacementIssues(ids, ids.Select(id => sheet.Bodies[id]).ToArray(),
            ids.Select(id => sheet.Fields[id]).ToArray(), sheet.Usable).Select(i => new ConnectedPlacementIssue(i.Code, i.Symbols)));
        foreach (var (id, at) in sheet.Before.OrderBy(p => p.Key))
            if (!sheet.After.TryGetValue(id, out var now) || now != at) issues.Add(new(ExistingSymbolMoved, [id]));
        foreach (var symbol in PartnerDistances(sheet, expectation).GroupBy(d => d.Symbol).OrderBy(g => g.Key))
            if (symbol.Min(d => d.DistanceNm) > NearestConnectionDistanceNm) issues.Add(new(ConnectionTooLong, [symbol.Key]));
        // Placement controls the room between symbols; a label that meets its own symbol is the realizer's concern. A new
        // symbol's room must be clear of everything else; an existing symbol's room must be clear of every new symbol and room.
        foreach (var room in sheet.Rooms)
        {
            bool fresh = expectation.NewSymbols.Contains(room.Owner);
            bool Relevant(Guid other) => fresh || expectation.NewSymbols.Contains(other);
            bool blocked = sheet.Fields.Any(f => f.Key != room.Owner && Relevant(f.Key) && (Overlap(room.Stub, f.Value) || Overlap(room.Label, f.Value)))
                || (fresh && sheet.Items.Any(i => Overlap(room.Stub, i.Bounds) || Overlap(room.Label, i.Bounds)))
                || sheet.Rooms.Any(o => o.Owner != room.Owner && Relevant(o.Owner) && (Overlap(room.Label, o.Label) || Overlap(room.Stub, o.Label)));
            if (blocked) issues.Add(new(ConnectionRoomBlocked, [room.Owner]));
        }
        return issues;
    }

    internal sealed record PartnerDistance(string Sheet, Guid Symbol, Guid Pin, PinEndpoint Endpoint, string Net, long DistanceNm);

    // For each connected pin of a new coordinate-free symbol, the Euclidean distance to the nearest same-net pin on this sheet
    // of a symbol whose position was already known (existing or explicitly placed), with the pin's net.
    internal static IEnumerable<PartnerDistance> PartnerDistances(ConnectedSheetGeometry sheet, ConnectedPlacementExpectation expectation)
    {
        foreach (var island in expectation.Intent.Screens.SelectMany(s => s.Islands).Where(i => i.SheetPathKey == sheet.PathKey))
        {
            var partners = island.Members.Where(m => m.Role != ConnectionMemberRole.ImplicitPower && !expectation.FreeSymbols.Contains(m.Pin.SymbolId))
                .Select(m => sheet.Pins[m.Pin.PlacedPinId]).ToArray();
            if (partners.Length == 0) continue;
            foreach (var member in island.Members.Where(m => expectation.FreeSymbols.Contains(m.Pin.SymbolId)))
            {
                var at = sheet.Pins[member.Pin.PlacedPinId];
                double nearest = partners.Min(p => Math.Sqrt(Math.Pow(p.XNm - at.XNm, 2) + Math.Pow(p.YNm - at.YNm, 2)));
                yield return new(sheet.Key, member.Pin.SymbolId, member.Pin.PlacedPinId, member.Pin.Endpoint,
                    expectation.Intent.Nets.Single(n => n.NetId == island.NetId).Name, (long)Math.Ceiling(nearest));
            }
        }
    }

    private static Dictionary<Guid, PresentationBounds> Moved(Dictionary<Guid, PresentationBounds> boxes, Guid id, PresentationPoint by) =>
        boxes.ToDictionary(p => p.Key, p => p.Key != id ? p.Value
            : new PresentationBounds(p.Value.LeftNm + by.XNm, p.Value.TopNm + by.YNm, p.Value.RightNm + by.XNm, p.Value.BottomNm + by.YNm));

    private static PresentationPoint Centre(PresentationBounds b) => new((b.LeftNm + b.RightNm) / 2, (b.TopNm + b.BottomNm) / 2);
    private static PresentationPoint Between(PresentationPoint from, PresentationPoint to) => new(to.XNm - from.XNm, to.YNm - from.YNm);

    private static PresentationBounds Presentation(Box2 box) =>
        new(box.Position.XNm, box.Position.YNm, box.Position.XNm + box.Size.XNm, box.Position.YNm + box.Size.YNm);

    // Open interiors overlap; touching edges do not.
    private static bool Overlap(PresentationBounds a, PresentationBounds b) =>
        a.LeftNm < b.RightNm && b.LeftNm < a.RightNm && a.TopNm < b.BottomNm && b.TopNm < a.BottomNm;

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
            // The CN-1 admission previews through the production MCP server, which records the attach-time handshake.
            connectionGate = await RequireConnectionRealizationGated();
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
            var advertised = RequireRealizationAdvertised(session);
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

        // CN-1 §6.3 (a) on this editor: a connection KiCad already shows without a label is named where it is, in two scenes
        // that do not depend on the identities a run generates. The editor first reloads its saved sheets. In each scene, in
        // one native step, TP1 and TP2 move together to free room if they must and the probe link is redrawn without its
        // SIGNAL label as one wire that leaves each probe pin straight in the direction a stub from it would take, so no join
        // stub has room on either pin (it would run along that wire) and the realizer must fall back to a label on a pin
        // itself. The realizer tries the link's pins in component order (§5.4); probes[0] below is the pin it tries first.
        // - join-turned-link: the wire turns one grid in front of the first pin, where a label on that pin would cross it, and
        //   runs six grids straight out of the second, so the label on the first pin is refused for exactly that turn and the
        //   second pin is named. The XML revision names the link LINK and adds the new probe TP900, a part of its own declared
        //   with a copy of the first-tried probe's definition, so the root sheet's library cache gains exactly that definition.
        // - join-anchor-label: the wire runs six grids straight out of both pins, so the first pin is named. The revision adds
        //   TP901, a new probe of the fixture probes' own part, which the planner creates by copying a placed probe. Every
        //   placed probe's reference sits elsewhere than the library puts it (the fixture's own, and those the field checks
        //   moved and dragged), and TP901's fields must sit exactly where the library puts them, whichever probe was copied. A
        //   copy that kept a dragged reference would be measured as covering all the room for its stub, and the realizer would
        //   rightly refuse it (§6.4 rule 6): the likely cause of the refusal in governed run t20260924T114705Z-b90471, whose
        //   recording was not kept.
        // Each new probe is measured live first, turned and placed where KiCad's own measurement leaves room for its stub and
        // label, and the realizer must draw exactly the predicted stub. Each realization is measured live and not applied
        // here (the NativeConnectedRealization journey applies realizations), kept as the replay fixture of that scene's name, and the redraw is
        // undone natively.
        async Task<object> RequireAnchorLabelJoin()
        {
            var session = await client.HandshakeAsync(token);
            var advertised = RequireRealizationAdvertised(session);
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
            // probes[0] is tried first; probes[1] second.
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
            var placed = probes.Select(p => (p.Owner, Pin: new SchematicPinAnchor(p.Pin) { Position = Moved(p.Pin.Position) })).ToArray();
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
            var screenId = Guid.Parse(rootScreen.Metadata.ScreenId.Value);
            static bool Meets((long L, long T, long R, long B) open, (long L, long T, long R, long B) closed) =>
                open.L < open.R && open.T < open.B && closed.L < open.R && closed.R > open.L && closed.T < open.B && closed.B > open.T;
            static bool Touches((long L, long T, long R, long B) a, (long L, long T, long R, long B) b) => a.L <= b.R && b.L <= a.R && a.T <= b.B && b.T <= a.B;
            static (long L, long T, long R, long B) Offset((long L, long T, long R, long B) a, long dx, long dy) => (a.L + dx, a.T + dy, a.R + dx, a.B + dy);
            static (long L, long T, long R, long B) Union((long L, long T, long R, long B) a, (long L, long T, long R, long B) b) =>
                (Math.Min(a.L, b.L), Math.Min(a.T, b.T), Math.Max(a.R, b.R), Math.Max(a.B, b.B));
            static (long L, long T, long R, long B) Inflate((long L, long T, long R, long B) a, long c) => (a.L - c, a.T - c, a.R + c, a.B + c);
            static Vector2 Relative(Vector2 at, Vector2 origin) => new() { XNm = at.XNm - origin.XNm, YNm = at.YNm - origin.YNm };

            // TP900's part: declared with a copy of the definition KiCad holds for the first-tried probe.
            var firstTried = rootScreen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Single(x => x.Id.Equals(probes[0].Owner.Id));
            var firstTriedLibrary = firstTried.LibraryId ?? firstTried.Definition.Id;
            string firstTriedKey = firstTried.LibName.Length != 0 ? firstTried.LibName
                : (firstTriedLibrary.LibraryNickname.Length == 0 ? "" : firstTriedLibrary.LibraryNickname + ":") + firstTriedLibrary.EntryName;
            var joinSymbol = rootScreen.CachedSymbols.Single(c => c.CacheKey == firstTriedKey).Clone();
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
            // TP901's part: the fixture probes' own, placed on this editor's sheets, which the planner copies.
            var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
            var linkParts = tried.Select(p => definitions[circuit.Components.Single(c => c.Id == p.ComponentId).DefinitionId].PartId).Distinct().ToArray();
            Assert.HasCount(1, linkParts, "Both fixture probes are one part.");
            var probePart = circuit.Parts.Single(p => p.Id == linkParts[0]);

            var turnedLink = await Realize("join-turned-link", [p0, Out(p0, grid), Out(Across(p0, bend), grid), Out(Across(p0, bend), depth), Out(p1, depth), p1],
                "TP900", joinPart, joinDeclaration, firstRefused: true);
            var anchorLabel = await Realize("join-anchor-label", [p0, Out(p0, depth), Out(p1, depth), p1], "TP901", probePart, null, firstRefused: false);
            return new { turnedLink, anchorLabel };

            // One scene: redraw the link along path, add the new probe of the given part (declared when declaration is set)
            // to it, place it where KiCad's measurement leaves it room, realize live, check, keep and undo.
            async Task<object> Realize(string name, Vector2[] path, string reference, PartDefinition newPart, SchematicPartSymbol? declaration, bool firstRefused)
            {
                var start = await Capture();
                Assert.IsTrue(SchematicHierarchyDelta.Plan(data, start.Electrical.Hierarchy.Data, token).Count == 0
                    && SchematicHierarchyDelta.Plan(start.Electrical.Hierarchy.Data, data, token).Count == 0, name + ": the scene starts from the saved sheets.");
                var wires = Enumerable.Range(0, path.Length - 1).Select(i => new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = path[i].Clone(),
                    End = path[i + 1].Clone(), Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked }).ToArray();
                var batch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = start.State.Revision.Epoch,
                    ExpectedRevision = start.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Redraw the probe link without its label" };
                if (shift != (0, 0))
                    foreach (var owner in probes.Select(p => p.Owner.Id.Value).Distinct())
                        batch.Operations.Add(new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Update = Any.Pack(Shifted(owner)) });
                batch.Operations.Add(new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Remove = oldLabel.Id.Clone() });
                batch.Operations.Add(new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Remove = oldWire.Id.Clone() });
                batch.Operations.Add(wires.Select(w => new SchematicItemOperation { TargetDocument = rootDocument.Clone(), Create = Any.Pack(w) }));
                var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(new() { Batch = batch, ExpectedState = start.State.Clone() }, token);
                Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorCode + ": " + receipt.ErrorMessage);
                var linked = await Capture();
                // KiCad itself joins exactly the two probes through the new wire, and nothing names that connection.
                var native = linked.Electrical.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == probes[0].Pin.Id.Value));
                CollectionAssert.AreEquivalent(new[] { probes[0].Pin.Id.Value, probes[1].Pin.Id.Value }.Concat(wires.Select(w => w.Id.Value)).ToArray(),
                    native.Sheets.SelectMany(s => s.Items).Select(i => i.Value).ToArray(), name + ": the redrawn link joins exactly the two probes.");

                // The record a synchronized editor holds for this redraw: the same model, KiCad's redrawn sheets. It plans settled.
                var baseline = current.State.Baseline with { Schematic = linked.Electrical.Hierarchy.Data.Clone() };
                var adopted = current.State with { NativeRevision = new(linked.State.Revision.Epoch, linked.State.Revision.Sequence),
                    TrackingComplete = linked.Electrical.Hierarchy.TrackingComplete, Baseline = baseline,
                    DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, [])), Observed = linked.Electrical.Hierarchy.Data.Clone(),
                    BaselineElectrical = linked.Electrical.Clone(), ObservedElectrical = linked.Electrical.Clone(), LastSynchronization = null };
                var settled = SchematicSynchronizationPlanner.Plan(adopted, advertised, token);
                Assert.IsTrue(settled.CanPrepare, settled.ErrorCode + ": " + settled.ErrorMessage);
                Assert.IsEmpty(settled.NativeOperations, name + ": the adopted redraw is settled.");

                // The XML revision names the link LINK and adds the new probe to it, turned and placed as given.
                Guid probeId = Guid.NewGuid(), probeDefinition = Guid.NewGuid();
                var probeOccurrences = Enumerable.Range(0, newPart.Units).Select(_ => Guid.NewGuid()).ToArray();
                var drawnPin = newPart.Pins.Where(p => p.Unit is 0 or 1).OrderBy(p => p.Number, StringComparer.Ordinal).First();
                var named = link with { Name = "LINK", Pins = [.. link.Pins, new PinEndpoint(probeId, drawnPin.Number)] };
                const long UnitPitch = 15_240_000;
                DesignRecoveryState Revision(long x, long y, int degrees) => adopted with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
                    baseline with { PartSymbols = declaration is null ? baseline.PartSymbols : [.. baseline.PartSymbols ?? [], declaration],
                        Engineering = baseline.Engineering with { Circuit = circuit with
                    {
                        Parts = declaration is null ? circuit.Parts : [.. circuit.Parts, newPart],
                        Components = [.. circuit.Components, new(probeId, probeDefinition, rootInstance.Id, reference)],
                        Sheets = circuit.Sheets.Select(s => s.Id == rootInstance.DefinitionId
                            ? s with { Components = [.. s.Components, new(probeDefinition, newPart.Id, "XML join probe")] } : s).ToArray(),
                        Symbols = [.. circuit.Symbols, .. probeOccurrences.Select((id, i) => new SymbolOccurrence(id, probeId, i + 1,
                            new(x / 1_000_000m, (y + i * UnitPitch) / 1_000_000m, degrees, false, false, false)))],
                        Nets = [.. circuit.Nets.Select(n => n.Id == link.Id ? named : n)]
                    } } }, [])) };
                Assert.IsNull(SchematicSynchronizationPlanner.Plan(Revision(30 * Step, 10 * Step, 0), token).Connections,
                    name + ": without a handshake the planner admits no wiring.");

                // The new probe as the planner creates it, measured live on the redrawn sheet in each orientation, with the label
                // its stub would carry: the first orientation in which some policy stub length keeps that label clear of the
                // probe's own bounds (every unit's) and the stub clear of its other units, and the first spot on the fixture grid
                // where all of the probe lies in the usable region clear of the redrawn link and its labels, that stub and label
                // keep clear of everything else, and its pins touch nothing. KiCad's placement is translation-invariant, so moving
                // the probe moves all of this with it.
                var provisional = SchematicSynchronizationPlanner.Plan(Revision(30 * Step, 10 * Step, 0), advertised, token);
                var provisionalIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(provisional);
                var units = provisional.Candidate!.Schematic.Instances.Single(x => x.Metadata.Document.Equals(rootDocument)).Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Where(x => provisionalIntent.CreatedSymbolIds.Contains(Guid.Parse(x.Id.Value))).OrderBy(x => x.Unit.Unit).ToArray();
                Assert.HasCount(newPart.Units, units);
                object fields;
                if (declaration is not null)
                {
                    Assert.AreEqual(declaration.Symbol.CacheKey, units[0].LibName, reference + " is drawn from its own declared definition.");
                    fields = new { declaredFrom = firstTriedKey };
                }
                else
                {
                    // Every placed probe the planner could copy has its reference elsewhere than the library puts it, so a copy
                    // that kept a placed probe's fields would show it; the new probe's fields sit exactly where the library puts them.
                    var templates = SchematicModelProjection.NativeSymbols(current.State.Baseline, data)
                        .Where(pair => circuit.Symbols.Single(s => s.Id == pair.Key) is var o
                            && definitions[circuit.Components.Single(c => c.Id == o.ComponentId).DefinitionId].PartId == newPart.Id)
                        .Select(pair => pair.Value).DistinctBy(s => s.Id.Value + "@" + string.Join('/', s.Path.Path.Select(p => p.Value))).ToArray();
                    Assert.IsNotEmpty(templates);
                    var library = units[0].Definition;
                    Assert.IsTrue(templates.All(t => !Relative(t.ReferenceField.Text.Position, t.Position).Equals(library.ReferenceField.Text.Position)),
                        "Every placed probe's reference sits elsewhere than the library puts it.");
                    foreach (var unit in units)
                        foreach (var (field, source) in new[] { (unit.ReferenceField, library.ReferenceField), (unit.ValueField, library.ValueField),
                            (unit.FootprintField, library.FootprintField), (unit.DatasheetField, library.DatasheetField), (unit.DescriptionField, library.DescriptionField) })
                        {
                            Assert.AreEqual(source.Text.Position, Relative(field.Text.Position, unit.Position), reference + "'s " + field.Name + " sits where the library puts it.");
                            Assert.AreEqual(source.Text.Attributes.Angle, field.Text.Attributes.Angle, reference + "'s " + field.Name + " takes the library's angle.");
                            Assert.AreEqual(source.Text.Attributes.HorizontalAlignment, field.Text.Attributes.HorizontalAlignment, field.Name);
                            Assert.AreEqual(source.Text.Attributes.VerticalAlignment, field.Text.Attributes.VerticalAlignment, field.Name);
                        }
                    var farthest = templates.Max(t => Math.Abs(t.ReferenceField.Text.Position.XNm - t.Position.XNm) + Math.Abs(t.ReferenceField.Text.Position.YNm - t.Position.YNm));
                    fields = new { copiedFromPlacedProbes = templates.Length, farthestPlacedReferenceNm = farthest,
                        libraryReference = new { library.ReferenceField.Text.Position.XNm, library.ReferenceField.Text.Position.YNm } };
                }
                var origin = units[0].Position.Clone();
                var addedPin = provisionalIntent.Screens.Single().Islands.Single().Members.Single(m => m.RequiresStub).Pin;
                var linkedModel = new KiCad.Automation.Model.DocumentRevision(linked.State.Revision.Epoch, linked.State.Revision.Sequence);
                var envelopes = new Dictionary<SchematicLabelSpinStyle, (long L, long T, long R, long B)>();
                {
                    var request = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = linked.State.Revision.Clone() };
                    var spins = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(SchematicConnectionGeometry.Spin).ToArray();
                    foreach (var spin in spins)
                        request.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Local,
                            SchematicConnectionIdentity.Probe(linkedModel, screenId, LocalLabel.Descriptor, "LINK", spin, SchematicLabelShape.SlshUnknown, probeId, 0),
                            new() { XNm = 40 * Step, YNm = 20 * Step }, "LINK", spin, policy)));
                    var labels = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                    for (int i = 0; i < spins.Length; i++)
                    {
                        var box = Rect(labels.ItemCandidates[i].Bounds);
                        var at = labels.ItemCandidates[i].Anchor;
                        envelopes[spins[i]] = (box.L - at.XNm, box.T - at.YNm, box.R - at.XNm, box.B - at.YNm);
                    }
                }
                (int Degrees, int Multiple, long X, long Y, (long X, long Y) Pin, (long X, long Y) End, (long L, long T, long R, long B) Label,
                    (long L, long T, long R, long B)[] Bounds, SchematicSymbolInstance[] Units)? choice = null;
                var tries = new List<object>();
                foreach (int degrees in new[] { 0, 180, 90, 270 })
                {
                    var request = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = linked.State.Revision.Clone() };
                    var turned = units.Select(u => { var t = u.Clone(); t.Transform = new() { Orientation = (SchematicSymbolOrientation)(degrees / 90 + 1) }; return t; }).ToArray();
                    request.Candidates.Add(turned.Select(t => t.Clone()));
                    var geometry = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
                    Assert.AreEqual(measured.Obstacles.Count, geometry.Obstacles.Count - wires.Length + 2, "Measuring " + reference + " measures the redrawn sheet.");
                    var bounds = geometry.Candidates.Select(c => Rect(c.Bounds)).ToArray();
                    var pins = geometry.Candidates.SelectMany(c => c.SymbolPins.Pins).ToArray();
                    Assert.IsTrue(geometry.Candidates.All(c => c.SymbolPins.Complete), reference + "'s pins are measured completely.");
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
                            // All of the probe stays inside the fixture's usable region; only its stub and label room must be clear.
                            if (moved.L < 10_000_000 || moved.T < 10_000_000 || moved.R > measured.PageBounds.Size.XNm - 10_000_000
                                || moved.B > measured.PageBounds.Size.YNm - 50_000_000) continue;
                            if (!Apart(moved, scene) || !Apart(Offset(room, dx, dy), scene) || !Free(Offset(room, dx, dy), linkItems)) continue;
                            if (!pins.All(p => Free((p.Position.XNm + dx, p.Position.YNm + dy, p.Position.XNm + dx, p.Position.YNm + dy), linkItems))) continue;
                            choice = (degrees, chosen, x, y, (pin.Position.XNm + dx, pin.Position.YNm + dy), (dx0 + dx, dy0 + dy), Offset(labelAt, dx, dy),
                                bounds.Select(b => Offset(b, dx, dy)).ToArray(), turned);
                        }
                    if (choice is not null) break;
                }
                Assert.IsNotNull(choice, "Some orientation and spot leave " + reference + " room for its stub: " + JsonSerializer.Serialize(tries));
                var chosenProbe = choice.Value;

                // The revision with the probe there. The planner creates exactly the measured symbol, moved and turned.
                var revision = Revision(chosenProbe.X, chosenProbe.Y, chosenProbe.Degrees);
                var plan = SchematicSynchronizationPlanner.Plan(revision, advertised, token);
                var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
                var created = plan.Candidate!.Schematic.Instances.Single(x => x.Metadata.Document.Equals(rootDocument)).Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Where(x => intent.CreatedSymbolIds.Contains(Guid.Parse(x.Id.Value))).OrderBy(x => x.Unit.Unit).ToArray();
                Assert.HasCount(newPart.Units, created);
                for (int i = 0; i < created.Length; i++)
                {
                    var expected = chosenProbe.Units[i].Clone();
                    long dx = chosenProbe.X - origin.XNm, dy = chosenProbe.Y - origin.YNm;
                    expected.Position = new() { XNm = expected.Position.XNm + dx, YNm = expected.Position.YNm + dy };
                    foreach (var field in new[] { expected.ReferenceField, expected.ValueField, expected.FootprintField, expected.DatasheetField, expected.DescriptionField }
                        .Concat(expected.UserFields).Where(f => f?.Text?.Position is not null))
                        field.Text.Position = new() { XNm = field.Text.Position.XNm + dx, YNm = field.Text.Position.YNm + dy };
                    Assert.AreEqual(expected, created[i], reference + " unit " + (i + 1) + " is the measured symbol, moved and turned.");
                }
                var island = intent.Screens.Single().Islands.Single();
                Assert.AreEqual("LINK", island.LabelText);
                Assert.IsFalse(island.AnchorHasMatchingDriver, "Nothing names the redrawn link.");
                Assert.IsTrue(island.JoinRequired);
                CollectionAssert.AreEqual(keys.Select(k => k.PlacedPinId).ToArray(), island.JoinCandidates.Select(p => p.PlacedPinId).ToArray(),
                    "The realizer tries the link's pins in component order, the first probe pin first.");
                var added = island.Members.Single(m => m.RequiresStub).Pin;
                Assert.AreEqual(new PinEndpoint(probeId, drawnPin.Number), added.Endpoint);
                Assert.IsTrue(added.CreatedSymbol);

                // The realizer's own §6.4 rule on a fresh live measurement of the redrawn sheet, with the probe as a candidate and
                // the label measured on each link pin.
                var judged = new MeasureSchematicPlacement { Document = rootDocument.Clone(), ExpectedRevision = linked.State.Revision.Clone() };
                judged.Candidates.Add(created.Select(c => c.Clone()));
                foreach (var candidate in island.JoinCandidates)
                {
                    var pin = placed.Single(p => p.Pin.Id.Value == candidate.PlacedPinId.ToString("D")).Pin;
                    var spin = SchematicConnectionGeometry.Spin(SchematicConnectionGeometry.Outward(pin));
                    var id = SchematicConnectionIdentity.Probe(linkedModel, screenId, LocalLabel.Descriptor, "LINK", spin, SchematicLabelShape.SlshUnknown, candidate.PlacedPinId, 0);
                    judged.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(ConnectionLabelKind.Local, id, pin.Position.Clone(), "LINK", spin, policy)));
                }
                var judgedGeometry = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(judged, token);
                var linkedRoot = linked.Electrical.Hierarchy.Data.Instances.Single(x => x.Metadata.Document.Equals(rootDocument));
                var islandItems = island.AnchorItemIds.Concat(island.Members.Select(m => m.Pin.PlacedPinId)).ToArray();
                var verdicts = island.JoinCandidates.Select((c, i) => (Candidate: c, Refusal: SchematicConnectionRealizer.AnchorLabelRefusal(linkedRoot, judgedGeometry, c.SymbolId,
                    judgedGeometry.Obstacles.Single(o => Guid.Parse(o.Id.Value) == c.SymbolId).SymbolPins.Pins.Single(p => Guid.Parse(p.Id.Value) == c.PlacedPinId),
                    judgedGeometry.ItemCandidates[i].Bounds, islandItems, policy))).ToArray();
                if (firstRefused)
                {
                    // Foreign points are taken in position order, then by owner: the turn holds the ends of the first two wires.
                    var turn = new[] { Guid.Parse(wires[0].Id.Value), Guid.Parse(wires[1].Id.Value) }.Order().First();
                    Assert.AreEqual("wireend point of " + turn.ToString("D") + " lies inside the label", verdicts[0].Refusal,
                        name + ": a label on the first probe pin is refused for the wire's turn in front of it.");
                }
                else Assert.IsNull(verdicts[0].Refusal, name + ": a label on the first probe pin is admitted.");
                Assert.IsNull(verdicts[1].Refusal, name + ": a label on the second probe pin is admitted.");

                var realization = await RealizeLive(name, adopted, revision, plan, advertised, name + ".recovery.json");
                var anchor = realization.Generated.Single(g => g.Role == GeneratedConnectionRole.AnchorLabel);
                Assert.IsFalse(realization.Generated.Any(g => g.Role == GeneratedConnectionRole.StubWire && island.JoinCandidates.Any(c => c.PlacedPinId == g.PlacedPinId)),
                    name + ": no join stub had room; each would run along the redrawn link.");
                int namedAt = firstRefused ? 1 : 0;
                Assert.AreEqual(island.JoinCandidates[namedAt].PlacedPinId, anchor.PlacedPinId,
                    firstRefused ? "After the first probe pin is refused, the second is named." : "The first probe pin is named.");
                var label = realization.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true).Select(o => o.Create.Unpack<LocalLabel>())
                    .Single(l => l.Id.Value == anchor.Id.ToString("D"));
                Assert.AreEqual(firstRefused ? p1 : p0, label.Position, name + ": the label sits on the named probe pin itself.");
                Assert.AreEqual(SchematicConnectionGeometry.Spin(outward), label.SpinStyle);
                Assert.AreEqual("LINK", label.Text.Text_);
                CollectionAssert.AreEquivalent(new[] { GeneratedConnectionRole.StubWire, GeneratedConnectionRole.StubLabel },
                    realization.Generated.Where(g => g.PlacedPinId == added.PlacedPinId).Select(g => g.Role).ToArray(), "The new probe gets its own stub and label.");
                var stubId = realization.Generated.Single(g => g.PlacedPinId == added.PlacedPinId && g.Role == GeneratedConnectionRole.StubWire).Id.ToString("D");
                var stubLabelId = realization.Generated.Single(g => g.PlacedPinId == added.PlacedPinId && g.Role == GeneratedConnectionRole.StubLabel).Id.ToString("D");
                var stubWire = realization.Operations.Where(o => o.Create?.Is(SchematicLine.Descriptor) == true).Select(o => o.Create.Unpack<SchematicLine>()).Single(w => w.Id.Value == stubId);
                var stubLabel = realization.Operations.Where(o => o.Create?.Is(LocalLabel.Descriptor) == true).Select(o => o.Create.Unpack<LocalLabel>()).Single(l => l.Id.Value == stubLabelId);
                Assert.AreEqual(new Vector2 { XNm = chosenProbe.Pin.X, YNm = chosenProbe.Pin.Y }, stubWire.Start, reference + "'s stub starts on its pin.");
                Assert.AreEqual(new Vector2 { XNm = chosenProbe.End.X, YNm = chosenProbe.End.Y }, stubWire.End,
                    reference + "'s stub has the length KiCad's measurement predicts: " + chosenProbe.Multiple + " grids.");
                Assert.AreEqual(stubWire.End, stubLabel.Position);
                Assert.HasCount(3, realization.Generated);
                Assert.AreEqual(newPart.Units, realization.Operations.Count(o => o.Create?.Is(SchematicSymbolInstance.Descriptor) == true), "The new probe is created with it.");
                // A batch that places a symbol always sends that sheet's whole library cache (SchematicItemDelta). The root sheet's
                // cache gains exactly the declared definition, or, for a copied probe whose definition the sheet already holds,
                // nothing; every definition it held is sent back exactly as KiCad holds it.
                var cache = realization.Operations.Single(o => o.ReplaceLibraryCache is not null).ReplaceLibraryCache;
                CollectionAssert.AreEquivalent(rootScreen.CachedSymbols.Select(c => c.CacheKey).Concat(declaration is null ? Array.Empty<string>() : new[] { declaration.Symbol.CacheKey }).ToArray(),
                    cache.Definitions.Select(d => d.CacheKey).ToArray(), declaration is null
                        ? "The root sheet already holds the copied probe's definition, so its cache gains nothing."
                        : "The root sheet's cache gains exactly " + reference + "'s definition.");
                foreach (var held in rootScreen.CachedSymbols)
                    Assert.IsTrue(SchematicLibraryCacheEquivalence.Equal(held, cache.Definitions.Single(d => d.CacheKey == held.CacheKey)),
                        "The root sheet's definition '" + held.CacheKey + "' is kept exactly as KiCad holds it.");
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
                var result = new { scene = name, depthNm = depth, turnNm = firstRefused ? grid : (long?)null, linkMoved = new { shift.Dx, shift.Dy },
                    blockedPin = firstRefused ? island.JoinCandidates[0].PlacedPinId : (Guid?)null, namedPin = anchor.PlacedPinId, anchorLabel = anchor.Id,
                    candidates = verdicts.Select(v => new { pin = v.Candidate.PlacedPinId, admitted = v.Refusal is null, whyNot = v.Refusal }).ToArray(),
                    labelPosition = new { label.Position.XNm, label.Position.YNm }, spin = label.SpinStyle.ToString(),
                    probe = new { reference, fields, orientationsTried = tries, rotation = chosenProbe.Degrees,
                        at = new { chosenProbe.X, chosenProbe.Y }, stubGrids = chosenProbe.Multiple,
                        stub = new { from = new { chosenProbe.Pin.X, chosenProbe.Pin.Y }, to = new { chosenProbe.End.X, chosenProbe.End.Y } } },
                    generated = realization.Generated.Select(g => g.Role.ToString()).ToArray(), undone = true };
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-" + name + ".json"), JsonSerializer.Serialize(result), token);
                return result;
            }
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
        // CN-1 admission (cn1-wiring-intent.md §4.1, §4.3, §8.3) against this editor's real handshake and captured state.
        // A saved XML revision that only connects drawn pins of the created components is admitted as a connected addition,
        // because this editor advertises schematic.connection-realization.v1. The public plan over the attached host is
        // exactly the planner's realization plan for the saved record: preparable, with no publishable XML and no native
        // operations (only the realization measures KiCad). Without a handshake the same revision keeps the general path.
        // KiCad, the recovery record and the XML file stay untouched.
        async Task<object> RequireConnectionRealizationGated()
        {
            var session = await client.HandshakeAsync(token);
            Assert.AreEqual(instanceId, session.InstanceId);
            RequireRealizationAdvertised(session);
            var current = store.Read()!;
            Assert.IsFalse(current.State.HasPendingWork);
            var circuit = current.State.Baseline.Engineering.Circuit;
            // The two probes created on the repeated channel sheet, whose shared pin every channel draws identically.
            var rootSheet = circuit.SheetInstances.Single(s => s.ParentId is null);
            var ends = circuit.Components.Where(c => createdIds.Contains(c.Id) && c.SheetInstanceId != rootSheet.Id).OrderBy(c => c.Id).ToArray();
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
            Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, real.Kind,
                "The real captured state must be recognized as a connected addition: " + real.ErrorCode + " " + real.ErrorMessage);
            CollectionAssert.AreEqual(new[] { probeNet }, real.ChangedNetIds.ToArray());
            Assert.IsEmpty(real.AddedComponentIds);
            var withoutHandshake = SchematicConnectedAddition.Classify(probe, probeDesign, null, token);
            Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, withoutHandshake.Kind, "Without a handshake nothing is admitted.");

            var nativeBefore = await Capture();
            byte[] fileBefore = await File.ReadAllBytesAsync(path, token);
            var planned = store.Save(probe, current.RevisionToken);
            // The planner's own result for the saved record and this editor's handshake is what the public tool must report.
            var expected = SchematicSynchronizationPlanner.Plan(store.Read()!.State, session, token);
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(expected);
            Assert.AreEqual("XML_CONNECTION_PROBE", intent.Nets.Single().Name);
            var general = SchematicSynchronizationPlanner.Plan(store.Read()!.State, token);
            Assert.IsTrue(general.CanPrepare, general.ErrorCode + ": " + general.ErrorMessage);
            Assert.IsNull(general.Connections, "Without a handshake the revision keeps the general path.");
            // The production MCP server previews with the handshake it recorded at attach. (The test-only synchronization
            // harness that serves this journey's other steps records no handshake, so its preview is the general path.)
            JsonElement plan;
            await using (var production = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-connection-gate-mcp"),
                Path.Combine(evidence, instanceId + "-connection-gate-mcp.stderr.log"), token))
            {
                RequireToolSuccess(await production.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                plan = await production.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = store.StatePath,
                    expectedRevisionToken = planned.RevisionToken });
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connection-gate-plan.json"), plan.GetRawText(), token);
            var planContent = plan.GetProperty("structuredContent");
            string? planCode = planContent.GetProperty("errorCode").GetString();
            RequireToolSuccess(plan);
            Assert.IsTrue(planContent.GetProperty("canPrepare").GetBoolean(), plan.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, planContent.GetProperty("errorCode").ValueKind, plan.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, planContent.GetProperty("candidateDesignXml").ValueKind,
                "A realization plan has no publishable preview (CN-1 §4.2): " + plan.GetRawText());
            Assert.AreEqual(0, planContent.GetProperty("nativeOperationsJson").GetArrayLength(), plan.GetRawText());
            Assert.IsTrue(planContent.GetProperty("nativeConnectivityValidationRequired").GetBoolean(), plan.GetRawText());
            Assert.AreEqual(expected.ErrorCode, planCode);
            Assert.AreEqual(expected.CandidateXml, planContent.GetProperty("candidateDesignXml").GetString());
            Assert.AreEqual(expected.NativeConnectivityValidationRequired, planContent.GetProperty("nativeConnectivityValidationRequired").GetBoolean());
            Assert.AreEqual(nativeBefore, await Capture(), "Planning a connected addition must not change the native document.");
            Assert.AreEqual(planned.RevisionToken, store.Read()!.RevisionToken, "Planning must not advance recovery.");
            CollectionAssert.AreEqual(fileBefore, await File.ReadAllBytesAsync(path, token), "Planning must not publish XML.");
            store.Save(current.State, planned.RevisionToken);
            var intents = await RequireRealConnectionIntents(store.Read()!.State, session, session);
            return new { nativeCapabilities = session.Capabilities.ToArray(), classification = real.Kind.ToString(),
                classificationWithoutHandshake = withoutHandshake.Kind.ToString(), changedNets = real.ChangedNetIds,
                publicPlanCanPrepare = planContent.GetProperty("canPrepare").GetBoolean(), publicPlanErrorCode = planCode,
                publicPlanNativeOperations = planContent.GetProperty("nativeOperationsJson").GetArrayLength(),
                publicPlanNativeConnectivityValidationRequired = planContent.GetProperty("nativeConnectivityValidationRequired").GetBoolean(),
                publicPlanIsRealizationPlan = true, generalPathCandidateXmlSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(general.CandidateXml!))),
                intents };
        }

        // CN-1 connection intent (cn1-wiring-intent.md §5) planned from this editor's real captured state with its own
        // handshake, which advertises schematic.connection-realization.v1; the general path is what planning without a
        // handshake gives. Each plan is inspected and realized from live measurements, never applied: nothing is saved,
        // published or sent.
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
                return (SchematicSynchronizationPlanner.Plan(revision, token), SchematicSynchronizationPlanner.Plan(revision, advertised, token), revision);
            }

            // The two channel probes share one physical pin, so joining them draws one hierarchical label on the
            // shared channel sheet and one sheet pin per channel on the root.
            var pairNet = new CircuitNet(Guid.NewGuid(), "XML_CHANNEL_PAIR", [.. channels.Select(c => new PinEndpoint(c.Id, drawnPin.Number))]);
            var pair = PlanNets([.. circuit.Nets, pairNet]);
            Assert.IsNull(pair.Real.Connections, "Without a handshake the planner admits no wiring.");
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

        // Realize a planned intent against this editor's own measurements without applying anything (applying goes through
        // synchronization and KiCad's connectivity assertion, proven by NativeConnectedRealization). The lane entry point must turn the same
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
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A realization that fails in any way (a refusal, a failed measurement or connection, or any other error) keeps
                // what the editor answered beside the saved record, so it can be replayed; only a refusal carries an error code.
                // If keeping that evidence fails as well, both failures are reported and the original failure is never hidden.
                string outcome = error is AutomationException ? ".refused.json" : ".failed.json";
                try
                {
                    await Keep();
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-realization-" + name + outcome),
                        SchematicConnectionRealizerTests.FormatRecording(name, revision, checkpoint, recorded, null, record, error), CancellationToken.None);
                }
                catch (Exception keeping)
                {
                    throw new AggregateException("Realizing " + name + " failed (" + (error is AutomationException refusal ? refusal.Code + ": " : "")
                        + error.Message + "), and keeping its evidence failed as well.", error, keeping);
                }
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
