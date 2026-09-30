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
    private static async Task VerifyRepeatedExternalOwners(NativeClient client, PsuCpuNativeContext context, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(evidence, instanceId)).FullName;
        string designPath = context.DesignPath;
        var store = new DesignRecoveryStore(Path.Combine(directory, "creation.json"));
        var root = context.Root; int step = 0;
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            Path.Combine(directory, "host"), Path.Combine(directory, "host.log"), token);
        async Task<JsonElement> Call(string name, object arguments)
        {
            int callStep = ++step;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await host.Tool(name, arguments);
                await File.WriteAllTextAsync(Path.Combine(directory, $"{callStep:D2}-{name}.json"), RetainedToolEvidence(result), token);
                return result;
            }
            finally
            {
                await File.AppendAllTextAsync(Path.Combine(directory, "operation-times.jsonl"),
                    JsonSerializer.Serialize(new { step = callStep, tool = name, elapsedMs = timer.ElapsedMilliseconds }) + "\n", CancellationToken.None);
            }
        }
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };
        RequireToolSuccess(await Call("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var initialized = await PsuCpuFixture.InitializeRecoveryAsync(client, context, store.StatePath, token);
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Components);
        var (regions, _, _) = await PsuCpuRegions(client, context.Baseline!, expected, (await Capture()).State.Revision, token);
        var desired = PsuCpuFixture.Desired(context, PsuCpuStage.Components);
        initialized = store.Save(initialized.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, [])) }, initialized.RevisionToken);
        var layout = await Call("kicad_design_propose_initial_layout", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = initialized.RevisionToken, gridNm = 1_270_000L, clearanceNm = 2_540_000L, pageInsetNm = 0L,
            regions, userInstructions = "Place the fixture parts on their declared sheets." });
        RequireToolSuccess(layout);
        string layoutXml = layout.GetProperty("structuredContent").GetProperty("desiredXml").GetString()!;
        await File.WriteAllTextAsync(designPath, layoutXml, new UTF8Encoding(false), token);
        store.Save(store.Read()!.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(layoutXml) }, store.Read()!.RevisionToken);
        await Apply();
        var created = store.Read()!.State.Baseline; var before = await Capture();
        PsuCpuFixture.AssertNative(created, before.Electrical, PsuCpuStage.Components);

        // Fixture setup: two explicit CPU-owned components already draw unit1.
        // Their unit4 drawings will be supplied by two new CPU_POWER instances.
        Guid processor = PsuCpuIds.Id(0x07, 7), cpuModel = PsuCpuIds.Id(0x05, 3), powerModel = PsuCpuIds.Id(0x05, 4);
        var component = created.Engineering.Circuit.Components.Single(c => c.Id == processor);
        var definition = created.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId);
        var cpu = Document(created, cpuModel); var power = Document(created, powerModel);
        var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            { Document = power.Clone(), ExpectedRevision = before.State.Revision.Clone() }, token);
        var powerPins = measured.Obstacles.Single(o => o.SymbolPins is not null).SymbolPins;
        Assert.IsTrue(powerPins.Complete);
        var points = powerPins.Pins.Where(p => p.PowerScope == SchematicPinPowerScope.SppsNone)
            .GroupBy(p => (p.Position.XNm, p.Position.YNm)).Select(g => g.First().Position).ToArray();
        var pair = points.SelectMany((p, i) => points.Skip(i + 1).Where(q => p.XNm == q.XNm || p.YNm == q.YNm)
            .Select(q => (First: p, Second: q, Length: Math.Abs(p.XNm - q.XNm) + Math.Abs(p.YNm - q.YNm))))
            .OrderBy(p => p.Length).First();
        var connectedPins = powerPins.Pins.Where(p => p.Position.Equals(pair.First) || p.Position.Equals(pair.Second))
            .Select(p => p.Number).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.IsTrue(connectedPins.Length >= 2);
        Guid templateOccurrence = created.Engineering.Circuit.Symbols.Single(s => s.ComponentId == processor && s.Unit == 1).Id;
        var template = NativeOf(created, before.Electrical.Hierarchy.Data, templateOccurrence);
        var copies = new[] { Place(template, "U900", cpu, template.Position.XNm + 127_000_000, template.Position.YNm),
            Place(template, "U901", cpu, template.Position.XNm + 254_000_000, template.Position.YNm) };
        var wire = new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") }, Type = SchematicLineType.SltWire,
            Start = pair.First.Clone(), End = pair.Second.Clone() };
        await Native([.. copies.Select(s => new SchematicItemOperation { TargetDocument = cpu.Clone(), Create = Any.Pack(s) }),
            new() { TargetDocument = power.Clone(), Create = Any.Pack(wire) }]);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root.Clone() }, token);
        var seeded = await Capture();
        var addedDefinitions = copies.Select(_ => definition with { Id = Guid.NewGuid() }).ToArray();
        var owners = copies.Select((s, i) => new ComponentInstance(Guid.NewGuid(), addedDefinitions[i].Id, cpuModel, s.ReferenceField.Text.Text_)).ToArray();
        var occurrences = copies.Select((s, i) => new SymbolOccurrence(Guid.NewGuid(), owners[i].Id, 1, SchematicModelProjection.Placement(s))).ToArray();
        Guid cpuDefinition = created.Engineering.Circuit.SheetInstances.Single(s => s.Id == cpuModel).DefinitionId;
        var baseline = created with { Schematic = seeded.Electrical.Hierarchy.Data.Clone(), Engineering = created.Engineering with
        { Circuit = created.Engineering.Circuit with
        {
            Sheets = created.Engineering.Circuit.Sheets.Select(s => s.Id == cpuDefinition ? s with { Components = [.. s.Components, .. addedDefinitions] } : s).ToArray(),
            Components = [.. created.Engineering.Circuit.Components, .. owners], Symbols = [.. created.Engineering.Circuit.Symbols, .. occurrences],
            Nets = [.. created.Engineering.Circuit.Nets, new(Guid.NewGuid(), "Original power-unit fixture connection",
                connectedPins.Select(pin => new PinEndpoint(processor, pin)).ToArray())]
        } }, SymbolBindings = [.. created.SymbolBindings, .. occurrences.Zip(copies).Select(p => new SchematicSymbolBinding(p.First.Id, Guid.Parse(p.Second.Id.Value)))] };
        Assert.IsTrue(SchematicElectricalComparison.Compare(baseline, seeded.Electrical, []).ConnectivityEquivalent);
        byte[] baselineXml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(designPath, baselineXml, token);
        store = new DesignRecoveryStore(Path.Combine(directory, "repeated-owners.json"));
        store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(seeded.State.Revision.Epoch, seeded.State.Revision.Sequence),
            seeded.Electrical.Hierarchy.TrackingComplete, baseline, baselineXml, seeded.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: seeded.Electrical.Clone(), ObservedElectrical: seeded.Electrical.Clone()), null);

        var powerParent = power.Clone(); powerParent.SheetPath.Path.RemoveAt(powerParent.SheetPath.Path.Count - 1);
        var originalSheet = seeded.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(powerParent)).Items
            .Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Equals(power.SheetPath.Path[^1]));
        var fileLocations = await client.InvokeAsync<ReadSchematicFileLocations, SchematicFileLocations>(new()
            { Document = root.Clone(), ExpectedRevision = seeded.State.Revision.Clone() }, token);
        NativeSheetFileLocations.Validate(fileLocations, seeded.Electrical.Hierarchy, client.Epoch);
        string originalFile = fileLocations.Locations.Single(f => f.Path.Equals(power.SheetPath)).LoadedFilename;
        string rootFile = fileLocations.Locations.Single(f => f.Path.Equals(root.SheetPath)).LoadedFilename;
        string sharedFilename = Path.GetRelativePath(Path.GetDirectoryName(rootFile)!, originalFile).Replace(Path.DirectorySeparatorChar, '/');
        var powerSymbol = seeded.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(power)).Items
            .Single(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        Guid powerNative = Guid.Parse(powerSymbol.Id.Value);
        var nativeSheet = NewSheet("Native repeated power", "90"); var nativeDocument = Child(nativeSheet.Id);
        var placement = powerSymbol.Clone(); placement.Path = nativeDocument.SheetPath.Clone(); placement.ReferenceField.Text.Text_ = owners[0].Reference;
        placement.InstanceRecords.Records.Add(Record(nativeDocument, owners[0].Reference));
        await Native(new() { TargetDocument = root.Clone(), Create = Any.Pack(nativeSheet) },
            new() { TargetDocument = nativeDocument.Clone(), Update = Any.Pack(placement) });
        RequireToolSuccess(await Call("kicad_design_recovery_refresh", Recovery()));
        var requested = await Call("kicad_design_sync_plan", Recovery());
        StringAssert.Contains(requested.GetProperty("structuredContent").GetProperty("errorMessage").GetString(), "kicad_design_repeated_sheet_answer");
        var question = requested.GetProperty("structuredContent").GetProperty("ownershipResolutionRequests").EnumerateArray().Single();
        Assert.AreEqual(SchematicNativeAdditionProjection.UnitOwnerAmbiguous, question.GetProperty("Code").GetString());
        CollectionAssert.Contains(question.GetProperty("CandidateComponentIds").EnumerateArray().Select(x => x.GetGuid()).ToArray(), owners[0].Id);
        object Choice(Guid owner) => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken,
            componentReferences = Array.Empty<object>(), symbolOwners = new[] { new { nativeObjectId = powerNative, componentId = owner,
                nativePath = nativeDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray() } } };
        var beforeChoice = await Capture(); byte[] beforeChoiceXml = await File.ReadAllBytesAsync(designPath, token);
        string beforeChoiceToken = store.Read()!.RevisionToken;
        var wrong = await Call("kicad_design_repeated_sheet_answer", Choice(owners[1].Id));
        Assert.IsTrue(wrong.GetProperty("isError").GetBoolean());
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, wrong.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.AreEqual(beforeChoiceToken, store.Read()!.RevisionToken); Assert.AreEqual(beforeChoice, await Capture());
        CollectionAssert.AreEqual(beforeChoiceXml, await File.ReadAllBytesAsync(designPath, token));
        var legacyAnswer = await Call("kicad_design_ownership_answer", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = beforeChoiceToken, designPath, answers = new[] { new { nativeObjectId = powerNative,
                componentId = owners[0].Id, nativePath = nativeDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray() } } });
        Assert.IsTrue(legacyAnswer.GetProperty("isError").GetBoolean());
        Assert.AreEqual(SchematicNativeAdditionProjection.AnswerInvalid, legacyAnswer.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        StringAssert.Contains(legacyAnswer.GetProperty("structuredContent").GetProperty("errorMessage").GetString(), "kicad_design_repeated_sheet_answer");
        Assert.AreEqual(beforeChoiceToken, store.Read()!.RevisionToken); Assert.AreEqual(beforeChoice, await Capture());
        CollectionAssert.AreEqual(beforeChoiceXml, await File.ReadAllBytesAsync(designPath, token));
        RequireToolSuccess(await Call("kicad_design_repeated_sheet_answer", Choice(owners[0].Id)));
        Assert.AreEqual(beforeChoice, await Capture()); CollectionAssert.AreEqual(beforeChoiceXml, await File.ReadAllBytesAsync(designPath, token));
        var adopted = await Publish();
        var nativeUnit = adopted.Engineering.Circuit.Symbols.Single(s => s.ComponentId == owners[0].Id && s.Unit == 4);
        Assert.AreEqual(cpuModel, adopted.Engineering.Circuit.Components.Single(c => c.Id == owners[0].Id).SheetInstanceId);
        Assert.AreEqual(ModelSheet(adopted, nativeDocument), nativeUnit.SheetInstanceId);
        Assert.IsTrue(Connected(adopted, owners[0].Id)); Assert.IsTrue(Connected(adopted, processor));
        Assert.IsNull(store.Read()!.State.RepeatedSheetResolution);
        await History(nativeDocument, nativeUnit.Id);

        // An explicitly bound XML candidate adds the other external unit. Its
        // component stays on CPU, while the new occurrence belongs to this path.
        var prior = store.Read()!.State.Baseline; var xmlSheet = NewSheet("XML repeated power", "91");
        var xmlDocument = Child(xmlSheet.Id); Guid xmlModel = Guid.NewGuid(), xmlOccurrence = Guid.NewGuid();
        var drawing = prior.Schematic.Clone();
        foreach (var screen in drawing.Instances.Where(s => s.Metadata.ScreenId.Equals(originalSheet.ChildScreenId)))
        {
            var index = screen.Items.Select((item, i) => (item, i)).Single(p => p.item.Is(SchematicSymbolInstance.Descriptor)).i;
            var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>(); symbol.InstanceRecords.Records.Add(Record(xmlDocument, owners[1].Reference));
            screen.Items[index] = Any.Pack(symbol);
        }
        var addedScreen = drawing.Instances.Single(s => s.Metadata.Document.Equals(power)).Clone();
        addedScreen.Metadata.Document = xmlDocument.Clone();
        var addedIndex = addedScreen.Items.Select((item, i) => (item, i)).Single(p => p.item.Is(SchematicSymbolInstance.Descriptor)).i;
        var addedSymbol = addedScreen.Items[addedIndex].Unpack<SchematicSymbolInstance>();
        addedSymbol.Path = xmlDocument.SheetPath.Clone(); addedSymbol.ReferenceField.Text.Text_ = owners[1].Reference;
        addedScreen.Items[addedIndex] = Any.Pack(addedSymbol); drawing.Instances.Add(addedScreen);
        drawing.Instances.Single(s => s.Metadata.Document.Equals(root)).Items.Add(Any.Pack(xmlSheet));
        var xmlDesired = prior with { Schematic = drawing, Engineering = prior.Engineering with { Circuit = prior.Engineering.Circuit with
        {
            SheetInstances = [.. prior.Engineering.Circuit.SheetInstances, new(xmlModel,
                prior.Engineering.Circuit.SheetInstances.Single(s => s.Id == powerModel).DefinitionId, ModelSheet(prior, root))],
            Symbols = [.. prior.Engineering.Circuit.Symbols, new(xmlOccurrence, owners[1].Id, 4, SchematicModelProjection.Placement(addedSymbol), xmlModel)],
            Nets = [.. prior.Engineering.Circuit.Nets, new(Guid.NewGuid(), "XML power-unit fixture connection",
                connectedPins.Select(pin => new PinEndpoint(owners[1].Id, pin)).ToArray())]
        } }, SheetBindings = [.. prior.SheetBindings, new(xmlModel, xmlDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())],
            SymbolBindings = [.. prior.SymbolBindings, new(xmlOccurrence, powerNative)] };
        string xml = SchematicDesignXml.Write(xmlDesired, []);
        await File.WriteAllTextAsync(designPath, xml, new UTF8Encoding(false), token);
        RequireToolSuccess(await Call("kicad_design_candidate_commit", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, candidateXml = xml,
            expectedCandidateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(xml))),
            operationId = Guid.NewGuid().ToString("D") }));
        var xmlApplied = await Publish();
        Assert.AreEqual(xmlModel, xmlApplied.Engineering.Circuit.Symbols.Single(s => s.Id == xmlOccurrence).SheetInstanceId);
        Assert.AreEqual(baseline.Engineering.Circuit.Components.Count, xmlApplied.Engineering.Circuit.Components.Count);
        Assert.IsTrue(Connected(xmlApplied, owners[0].Id) && Connected(xmlApplied, owners[1].Id) && Connected(xmlApplied, processor));
        await History(xmlDocument, xmlOccurrence);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var reloaded = await Capture();
        RequireToolSuccess(await Call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = reloaded.State.Revision.Epoch }));
        var final = await Publish();
        Assert.AreEqual(owners[0].Id, final.Engineering.Circuit.Symbols.Single(s => s.Id == nativeUnit.Id).ComponentId);
        Assert.AreEqual(owners[1].Id, final.Engineering.Circuit.Symbols.Single(s => s.Id == xmlOccurrence).ComponentId);
        byte[] stableXml = await File.ReadAllBytesAsync(designPath, token); await Publish();
        CollectionAssert.AreEqual(stableXml, await File.ReadAllBytesAsync(designPath, token));
        await PopulatedParent();
        await File.WriteAllTextAsync(Path.Combine(directory, "external-owner-proof.json"), JsonSerializer.Serialize(new
        { instanceId, nativeChoice = true, wrongOwnerRefused = true, legacyAnswerRefusedWithoutWriting = true, xmlExternalOwner = true,
            noOwnerClones = true, separateUnitLocations = true, separateConnectionsPreserved = true,
            undoRedoExact = true, reloadExact = true, repeatNoOp = true,
            populatedParentMultiUnit = true, populatedParentConnected = true, populatedParentHistory = true,
            populatedParentExistingOwnerConnected = true }), token);

        async Task PopulatedParent()
        {
            // Capture KiCad's complete label representation in this disposable
            // fixture, then remove the probe before planning the real edit.
            var probe = new GlobalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
                Position = new() { XNm = -254000000, YNm = -254000000 },
                Text = new() { Text_ = "PARENT_LINK_PROBE", Position = new() { XNm = -254000000, YNm = -254000000 },
                    Attributes = new() { Size = new() { XNm = 1270000, YNm = 1270000 },
                        HorizontalAlignment = HorizontalAlignment.HaLeft, VerticalAlignment = VerticalAlignment.VaCenter } },
                Shape = SchematicLabelShape.SlshBidi, SpinStyle = SchematicLabelSpinStyle.SlssRight };
            await Native(new SchematicItemOperation { TargetDocument = cpu.Clone(), Create = Any.Pack(probe) });
            var labelTemplate = (await Capture()).Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(cpu)).Items
                .Where(i => i.Is(GlobalLabel.Descriptor)).Select(i => i.Unpack<GlobalLabel>()).Single(l => l.Id.Equals(probe.Id));
            await Native(new SchematicItemOperation { TargetDocument = cpu.Clone(), Remove = probe.Id.Clone() });
            await Publish();
            var previous = store.Read()!.State.Baseline;
            Guid movedModel = ModelSheet(previous, nativeDocument), parentModel = Guid.NewGuid(), parentDefinition = Guid.NewGuid();
            var drawing = previous.Schematic.Clone();
            var rootScreen = drawing.Instances.Single(s => s.Metadata.Document.Equals(root));
            var originalReference = rootScreen.Items.Single(i => i.Is(SheetSymbol.Descriptor) && i.Unpack<SheetSymbol>().Id.Equals(nativeSheet.Id));
            var movedReference = originalReference.Unpack<SheetSymbol>(); rootScreen.Items.Remove(originalReference);
            var parentReference = NewSheet("Populated parent", "93"); parentReference.Id.Value = Guid.NewGuid().ToString("D");
            parentReference.ChildScreenId.Value = Guid.NewGuid().ToString("D"); parentReference.FilenameField.Text.Text_ = "populated-parent.kicad_sch";
            var parentDocument = Child(parentReference.Id);
            var movedDocument = parentDocument.Clone(); movedDocument.SheetPath.Path.Add(nativeSheet.Id.Clone());
            movedReference.Path = parentDocument.SheetPath.Clone();
            foreach (var record in movedReference.InstanceRecords.Records)
                if (record.Path.SequenceEqual(root.SheetPath.Path))
                { record.Path.Clear(); record.Path.Add(parentDocument.SheetPath.Path.Select(p => p.Clone())); }
            foreach (var screen in drawing.Instances)
            {
                bool movedScreen = screen.Metadata.Document.Equals(nativeDocument);
                if (movedScreen) screen.Metadata.Document = movedDocument.Clone();
                for (int i = 0; i < screen.Items.Count; ++i)
                    if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
                    {
                        var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                        if (movedScreen) symbol.Path = movedDocument.SheetPath.Clone();
                        foreach (var record in symbol.InstanceRecords.Records)
                            if (record.Path.SequenceEqual(nativeDocument.SheetPath.Path))
                            { record.Path.Clear(); record.Path.Add(movedDocument.SheetPath.Path.Select(p => p.Clone())); }
                        var ordered = symbol.InstanceRecords.Records.OrderBy(r => r.Path.Count)
                            .ThenBy(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal).ToArray();
                        symbol.InstanceRecords.Records.Clear(); symbol.InstanceRecords.Records.Add(ordered); screen.Items[i] = Any.Pack(symbol);
                    }
            }
            var parentScreen = new SchematicScreenData { Metadata = rootScreen.Metadata.Clone() };
            parentScreen.Metadata.Document = parentDocument.Clone(); parentScreen.Metadata.ScreenId = parentReference.ChildScreenId.Clone();
            parentScreen.Metadata.RootInstance = new(); parentScreen.Metadata.LoadedNativeFormatVersion = 0;
            parentScreen.Items.Add(Any.Pack(movedReference)); drawing.Instances.Add(parentScreen); rootScreen.Items.Add(Any.Pack(parentReference));
            var scaffold = previous with { Schematic = drawing, Engineering = previous.Engineering with { Circuit = previous.Engineering.Circuit with
            { Sheets = [.. previous.Engineering.Circuit.Sheets, new(parentDefinition, "Populated parent", [])],
                SheetInstances = [.. previous.Engineering.Circuit.SheetInstances.Select(s => s.Id == movedModel ? s with { ParentId = parentModel } : s),
                    new(parentModel, parentDefinition, ModelSheet(previous, root))] } },
                SheetBindings = [.. previous.SheetBindings.Select(b => b.SheetInstanceId == movedModel
                    ? b with { NativePath = movedDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray() } : b),
                    new(parentModel, parentDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())] };
            var populated = SchematicRebuildTests.WithBoundParentComponent(scaffold, parentModel, processor, "U950");
            var newComponent = populated.Engineering.Circuit.Components.Single(c => c.Reference == "U950");
            var newSymbols = SchematicModelProjection.NativeSymbols(populated, populated.Schematic);
            var powerOccurrence = populated.Engineering.Circuit.Symbols.Single(s => s.ComponentId == newComponent.Id && s.Unit == 4);
            var powerDrawing = newSymbols[powerOccurrence.Id].Clone();
            powerDrawing.Path = cpu.SheetPath.Clone();
            powerDrawing.InstanceRecords.Records.Single().Path.Clear(); powerDrawing.InstanceRecords.Records.Single().Path.Add(cpu.SheetPath.Path.Select(p => p.Clone()));
            var at = await Capture();
            var existingOccurrence = previous.Engineering.Circuit.Symbols.Single(s => s.ComponentId == processor && s.Unit == 1);
            var existingDrawing = SchematicModelProjection.NativeSymbols(previous, previous.Schematic)[existingOccurrence.Id];
            var geometry = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                { Document = cpu.Clone(), ExpectedRevision = at.State.Revision.Clone(), Candidates = { powerDrawing } }, token);
            var pinGeometry = geometry.Candidates[0].SymbolPins;
            Assert.IsTrue(pinGeometry.Complete);
            var endpoints = pinGeometry.Pins.Where(p => p.PowerScope == SchematicPinPowerScope.SppsNone)
                .GroupBy(p => (p.Position.XNm, p.Position.YNm)).Select(g => g.First().Position).ToArray();
            var wireEnds = endpoints.SelectMany((p, i) => endpoints.Skip(i + 1).Where(q => p.XNm == q.XNm || p.YNm == q.YNm)
                .Select(q => (First: p, Second: q, Length: Math.Abs(p.XNm - q.XNm) + Math.Abs(p.YNm - q.YNm)))).OrderBy(p => p.Length).First();
            var pins = pinGeometry.Pins.Where(p => p.Position.Equals(wireEnds.First) || p.Position.Equals(wireEnds.Second))
                .Select(p => p.Number).Distinct(StringComparer.Ordinal).ToArray();
            var populatedScreen = populated.Schematic.Instances.Single(s => s.Metadata.Document.Equals(parentDocument));
            populatedScreen.Items.Add(Any.Pack(new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") },
                Type = SchematicLineType.SltWire, Start = wireEnds.First.Clone(), End = wireEnds.Second.Clone(), Locked = LockedState.LsUnlocked,
                Stroke = previous.Schematic.Instances.Single(s => s.Metadata.Document.Equals(power)).Items
                    .First(i => i.Is(SchematicLine.Descriptor)).Unpack<SchematicLine>().Stroke.Clone() }));
            // Existing symbols are measured as native obstacles. Candidates are
            // detached proposals and must have identities absent from the sheet.
            var oldPins = geometry.Obstacles.Single(o => o.Id.Equals(existingDrawing.Id)).SymbolPins;
            Assert.IsTrue(oldPins.Complete);
            var oldGroup = oldPins.Pins.GroupBy(p => (p.Position.XNm, p.Position.YNm))
                .First(g => g.All(p => p.PowerScope == SchematicPinPowerScope.SppsNone
                    && !previous.Engineering.Circuit.Nets.SelectMany(n => n.Pins).Contains(new PinEndpoint(processor, p.Number))));
            GlobalLabel Link(Vector2 position)
            {
                var label = labelTemplate.Clone(); label.Id.Value = Guid.NewGuid().ToString("D");
                long dx = position.XNm - label.Position.XNm, dy = position.YNm - label.Position.YNm;
                label.Position = position.Clone(); label.Text.Text_ = "PopulatedParentLink";
                label.Text.Position.XNm += dx; label.Text.Position.YNm += dy;
                label.IntersheetRefsField.Text.Position.XNm += dx; label.IntersheetRefsField.Text.Position.YNm += dy;
                return label;
            }
            populatedScreen.Items.Add(Any.Pack(Link(wireEnds.First)));
            populated.Schematic.Instances.Single(s => s.Metadata.Document.Equals(cpu)).Items.Add(Any.Pack(Link(oldGroup.First().Position)));
            var connectedEndpoints = pins.Select(p => new PinEndpoint(newComponent.Id, p))
                .Concat(oldGroup.Select(p => new PinEndpoint(processor, p.Number))).Distinct().ToArray();
            populated = populated with { Engineering = populated.Engineering with { Circuit = populated.Engineering.Circuit with
                { Nets = [.. populated.Engineering.Circuit.Nets, new(Guid.NewGuid(), "PopulatedParentLink", connectedEndpoints)] } } };
            var invalid = populated with { Engineering = populated.Engineering with { Circuit = populated.Engineering.Circuit with
                { Components = populated.Engineering.Circuit.Components.Select(c => c.Id == newComponent.Id ? c with { Reference = "U951" } : c).ToArray() } } };
            await CommitXml(invalid);
            var beforeRefusal = await Capture(); var rejected = await Call("kicad_design_sync_plan", Recovery());
            Assert.IsTrue(rejected.GetProperty("isError").GetBoolean()); Assert.AreEqual(beforeRefusal, await Capture());
            Assert.AreEqual(SchematicDesignXml.Write(invalid, []), await File.ReadAllTextAsync(designPath, token));
            await CommitXml(populated);
            var planned = await Call("kicad_design_sync_plan", Recovery()); RequireToolSuccess(planned);
            var operations = planned.GetProperty("structuredContent").GetProperty("nativeOperationsJson").EnumerateArray()
                .Select(p => SchematicJson.Parser.Parse<SchematicItemOperation>(p.GetString()!)).ToArray();
            var rollback = new CheckedSchematicBatch { ExpectedState = beforeRefusal.State.Clone(), Batch = new()
            { Document = root.Clone(), DocumentEpoch = beforeRefusal.State.Revision.Epoch, ExpectedRevision = beforeRefusal.State.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), Description = "Reject populated-parent move atomically" } };
            rollback.Batch.Operations.Add(operations); rollback.Batch.Operations.Add(new SchematicItemOperation());
            var refused = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(rollback, token);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, refused.Status); Assert.AreEqual(beforeRefusal, await Capture());
            var complete = await Publish();
            Assert.AreEqual(newComponent, complete.Engineering.Circuit.Components.Single(c => c.Id == newComponent.Id));
            Assert.HasCount(4, complete.Engineering.Circuit.Symbols.Where(s => s.ComponentId == newComponent.Id));
            Assert.AreEqual(movedModel, ModelSheet(complete, movedDocument));
            foreach (var old in previous.Engineering.Circuit.Components) Assert.AreEqual(old, complete.Engineering.Circuit.Components.Single(c => c.Id == old.Id));
            await History(parentDocument, powerOccurrence.Id);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
            var observed = await Capture();
            RequireToolSuccess(await Call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = observed.State.Revision.Epoch }));
            var reopened = await Publish(); Assert.AreEqual(newComponent, reopened.Engineering.Circuit.Components.Single(c => c.Id == newComponent.Id));
            byte[] settled = await File.ReadAllBytesAsync(designPath, token); await Publish(); CollectionAssert.AreEqual(settled, await File.ReadAllBytesAsync(designPath, token));

            async Task CommitXml(SchematicDesign candidate)
            {
                string text = SchematicDesignXml.Write(candidate, []); await File.WriteAllTextAsync(designPath, text, new UTF8Encoding(false), token);
                RequireToolSuccess(await Call("kicad_design_candidate_commit", new { instanceId, recoveryPath = store.StatePath,
                    expectedRevisionToken = store.Read()!.RevisionToken, candidateXml = text,
                    expectedCandidateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                    operationId = Guid.NewGuid().ToString("D") }));
            }
        }

        bool Connected(SchematicDesign design, Guid owner) => design.Engineering.Circuit.Nets.Any(n =>
            n.Pins.ToHashSet().SetEquals(connectedPins.Select(pin => new PinEndpoint(owner, pin))));

        DocumentSpecifier Document(SchematicDesign design, Guid id)
        { var d = root.Clone(); d.SheetPath.Path.Clear(); d.SheetPath.Path.Add(design.SheetBindings.Single(b => b.SheetInstanceId == id).NativePath.Select(p => new KIID { Value = p.ToString("D") })); return d; }
        DocumentSpecifier Child(KIID id) { var d = root.Clone(); d.SheetPath.Path.Add(id.Clone()); return d; }
        Guid ModelSheet(SchematicDesign design, DocumentSpecifier document) => design.SheetBindings.Single(b =>
            b.NativePath.SequenceEqual(document.SheetPath.Path.Select(p => Guid.Parse(p.Value)))).SheetInstanceId;
        SymbolSheetRecord Record(DocumentSpecifier document, string reference)
        { var r = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = reference, Unit = 4, Variants = new() }; r.Path.Add(document.SheetPath.Path.Select(p => p.Clone())); return r; }
        SheetSymbol NewSheet(string name, string page)
        {
            var s = originalSheet.Clone();
            // Retain the failing path order from9640df instead of relying on random UUID ordering.
            s.Id.Value = page == "90" ? "c7bf55e7-9cd6-419a-b2c7-3fd9983f6233" : "d0c6611e-acb3-44d4-99fe-4df68aa052ad";
            s.NameField.Text.Text_ = name; s.PageNumber = page;
            s.FilenameField.Text.Text_ = sharedFilename;
            s.Path = root.SheetPath.Clone(); var r = new SheetPlacementRecord { ProjectName = root.Project.Name, PageNumber = page, Variants = new() };
            r.Path.Add(root.SheetPath.Path.Select(p => p.Clone())); s.InstanceRecords = new() { Records = { r } }; return s;
        }
        async Task Native(params SchematicItemOperation[] operations)
        {
            var at = await Capture(); var batch = new CheckedSchematicBatch { ExpectedState = at.State.Clone(), Batch = new()
            { Document = root.Clone(), DocumentEpoch = at.State.Revision.Epoch, ExpectedRevision = at.State.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), Description = "Verify repeated-sheet external ownership" } };
            batch.Batch.Operations.Add(operations);
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(batch, token);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
        }
        async Task Apply()
        {
            var result = await Call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            if (result.TryGetProperty("isError", out var error) && error.GetBoolean())
            {
                try
                {
                    var actual = await Capture();
                    await File.WriteAllTextAsync(Path.Combine(directory, "failed-native.json"), SchematicJson.Formatter.Format(actual), token);
                    if (store.Read()?.State.PendingPublication is { } pending)
                    {
                        await File.WriteAllBytesAsync(Path.Combine(directory, "failed-candidate.xml"), pending.CandidateFileBytes, token);
                        var candidate = SchematicDesignXml.Read(Encoding.UTF8.GetString(pending.CandidateFileBytes), []);
                        var delta = SchematicHierarchyDelta.Plan(actual.Electrical.Hierarchy.Data, candidate.Schematic);
                        await File.WriteAllTextAsync(Path.Combine(directory, "failed-delta.json"),
                            JsonSerializer.Serialize(delta.Select(operation => SchematicJson.Formatter.Format(operation)).ToArray()), token);
                    }
                }
                catch (Exception captureError)
                { await File.WriteAllTextAsync(Path.Combine(directory, "failure-capture-error.txt"), captureError.ToString(), CancellationToken.None); }
            }
            RequireToolSuccess(result);
        }
        async Task<SchematicDesign> Publish()
        {
            RequireToolSuccess(await Call("kicad_design_recovery_refresh", Recovery()));
            RequireToolSuccess(await Call("kicad_design_sync_plan", Recovery())); await Apply();
            var result = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            var comparison = SchematicElectricalComparison.Compare(result, (await Capture()).Electrical, []);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent);
            CollectionAssert.AreEqual(store.Read()!.State.DesiredFileBytes, await File.ReadAllBytesAsync(designPath, token)); return result;
        }
        async Task History(DocumentSpecifier document, Guid occurrence)
        {
            await FocusedSchematicShortcut(client, root, processId, display, "z", token);
            await WaitSheet(document, false);
            var undone = await Publish(); Assert.IsFalse(undone.Engineering.Circuit.Symbols.Any(s => s.Id == occurrence));
            Assert.IsFalse(undone.SheetBindings.Any(b => b.NativePath.SequenceEqual(document.SheetPath.Path.Select(p => Guid.Parse(p.Value)))));
            await FocusedSchematicShortcut(client, root, processId, display, "y", token);
            await WaitSheet(document, true);
            var redone = await Publish(); Assert.IsTrue(redone.Engineering.Circuit.Symbols.Any(s => s.Id == occurrence));
        }
        async Task WaitSheet(DocumentSpecifier document, bool exists)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await Capture()).Electrical.Hierarchy.Data.Instances.Any(s => s.Metadata.Document.Equals(document)) != exists)
                await Task.Delay(50, limit.Token);
        }
    }
}
