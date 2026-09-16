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
        GlobalLabel Label(Vector2 position) => new() { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = position.Clone(),
            Text = new() { Text_ = "CROSS_SHEET_LINK", Position = position.Clone(), Attributes = new()
                { Size = new() { XNm = 1270000, YNm = 1270000 }, HorizontalAlignment = HorizontalAlignment.HaLeft,
                    VerticalAlignment = VerticalAlignment.VaCenter } }, Shape = SchematicLabelShape.SlshBidi, SpinStyle = SchematicLabelSpinStyle.SlssRight };
        var rootBatch = new ApplySchematicItemBatch { Document = root.Clone(), Description = "Cross-sheet component root units" };
        for (int index = 0; index < 2; index++)
        {
            var symbol = Symbol(index, root, 1, 150000000 + index * 30000000L);
            rootBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
            rootBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(Label(symbol.Position)) });
        }
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rootBatch, token);
        var shared = Symbol(2, first, 2, 150000000);
        var childBatch = new ApplySchematicItemBatch { Document = first.Clone(), Description = "Cross-sheet shared second unit" };
        childBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(shared) });
        childBatch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(Label(shared.Position)) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(childBatch, token);

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
        // The separate service-restart fixture uses the standard title-block
        // command, whose existing contract requires the displayed sheet. The
        // offscreen-view preservation assertions above have already completed.
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
        await VerifySynchronizationServiceRestart(client, root, store, designPath, evidence, instanceId, token,
            ["publication-replaced", "baseline-committed", "retained-archived"]);
        await VerifyAutomaticSynchronization(client, root, store, designPath, evidence, instanceId, token);
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

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
    }
}
