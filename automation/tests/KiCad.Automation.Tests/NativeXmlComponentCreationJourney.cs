using System.Text;
using System.Text.Json;
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
            original.Definition.UnitCount = 2;
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
            part = part with { Id = Guid.NewGuid(), Name = "Explicit declared two-unit probe", Units = 2,
                Pins = [.. part.Pins.Select(p => p with { Unit = 1 }), new(extra.Number, extra.Name, 2)] };
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
        }
        await RequireAgreement(createdIds.Count);
        await VerifyCreatedGeometry(client, document, geometry, token);
        var created = store.Read()!.State.Baseline;
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
            crossSheetRejection,
            crossPlatformReady = false }), token);

        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

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
