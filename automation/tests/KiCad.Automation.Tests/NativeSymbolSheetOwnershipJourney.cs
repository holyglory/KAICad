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
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9).
    // Lane 2C replaces this body when it delivers the journey.
    private static Task VerifyPsuCpuOwnershipSync(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
        => throw new AssertInconclusiveException("Phase 2 lane 2C has not delivered this journey");

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
