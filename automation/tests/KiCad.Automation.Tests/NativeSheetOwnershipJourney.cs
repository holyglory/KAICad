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
    // Existing repeated-screen fixture: native hierarchy changes and the inverse
    // XML edits must converge through the public tools, retaining exact identities.
    private static async Task VerifySheetOwnershipRoundTrips(NativeClient client, DocumentSpecifier root,
        HierarchyFixture fixture, int processId, string display, string instanceId, string designPath,
        DesignRecoveryStore store, Func<string, object, Task<JsonElement>> call, CancellationToken token)
    {
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };
        async Task<SchematicDesign> Publish(string phase, bool nativeMutation = false)
        {
            RequireToolSuccess(await call("kicad_design_recovery_refresh", Recovery()));
            var plan = await call("kicad_design_sync_plan", Recovery()); RequireToolSuccess(plan);
            var applied = await call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            if (applied.TryGetProperty("isError", out var error) && error.GetBoolean())
            {
                string directory = Path.GetDirectoryName(designPath)!;
                var actual = await Capture();
                await File.WriteAllTextAsync(Path.Combine(directory, phase + "-actual.xml"), SchematicDataXml.Write(actual.Electrical.Hierarchy.Data), token);
                if (store.Read()!.State.PendingPublication is { } pending)
                    await File.WriteAllBytesAsync(Path.Combine(directory, phase + "-candidate.xml"), pending.CandidateFileBytes, token);
            }
            RequireToolSuccess(applied);
            Assert.AreEqual(nativeMutation, applied.GetProperty("structuredContent").GetProperty("nativeMutationCommitted").GetBoolean(), phase);
            var design = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            var binding = SchematicDesignBindings.Inspect(design, []);
            Assert.IsTrue(binding.IdentitiesResolved, phase + ": " + string.Join(',', binding.Issues.Select(i => i.Code)));
            var electrical = SchematicElectricalComparison.Compare(design, (await Capture()).Electrical, []);
            Assert.IsTrue(electrical.PinBindingsComplete && electrical.ConnectivityEquivalent, phase);
            return design;
        }
        async Task Native(bool shouldCommit, params SchematicItemOperation[] operations)
        {
            var state = await Capture();
            var request = new CheckedSchematicBatch { ExpectedState = state.State.Clone(), Batch = new()
            {
                Document = root.Clone(), DocumentEpoch = state.State.Revision.Epoch, ExpectedRevision = state.State.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D"), Description = "Change the native sheet hierarchy"
            } };
            request.Batch.Operations.Add(operations);
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
            if (shouldCommit) Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
            else
            {
                Assert.AreNotEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status);
                var after = await Capture();
                Assert.AreEqual(state.State.StateSha256, after.State.StateSha256, "Rejected reparent restores all path-specific records.");
                Assert.AreEqual(state.State.Revision, after.State.Revision);
                CollectionAssert.AreEqual(PinPartitions(state.Electrical), PinPartitions(after.Electrical));
            }
        }
        DocumentSpecifier Child(DocumentSpecifier parent, KIID id)
        { var result = parent.Clone(); result.SheetPath.Path.Add(id.Clone()); return result; }
        SchematicSheetBinding Binding(SchematicDesign design, DocumentSpecifier document) => design.SheetBindings.Single(b =>
            b.NativePath.SequenceEqual(document.SheetPath.Path.Select(p => Guid.Parse(p.Value))));
        SheetSymbol Symbol(CheckedSchematicState state, DocumentSpecifier parent, KIID id) => state.Electrical.Hierarchy.Data.Instances
            .Single(s => s.Metadata.Document.Equals(parent)).Items.Where(i => i.Is(SheetSymbol.Descriptor))
            .Select(i => i.Unpack<SheetSymbol>()).Single(s => s.Id.Equals(id));
        async Task<SchematicDesign> Xml(SchematicDesign desired, string phase)
        {
            string candidateXml = SchematicDesignXml.Write(desired, []);
            await File.WriteAllTextAsync(designPath, candidateXml, new UTF8Encoding(false), token);
            RequireToolSuccess(await call("kicad_design_candidate_commit", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, candidateXml,
                expectedCandidateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(candidateXml))),
                operationId = Guid.NewGuid().ToString("D") }));
            return await Publish(phase, nativeMutation: true);
        }
        var original = await Publish("initial");
        var before = await Capture();
        var nativeFiles = await client.InvokeAsync<ReadSchematicFileLocations, SchematicFileLocations>(new()
            { Document = root.Clone(), ExpectedRevision = before.State.Revision.Clone() }, token);
        NativeSheetFileLocations.Validate(nativeFiles, before.Electrical.Hierarchy, client.Epoch);
        Assert.AreEqual(before, await Capture(), "File-location observation changes neither content nor editor context.");
        var staleFileRead = new ReadSchematicFileLocations { Document = root.Clone(), ExpectedRevision = before.State.Revision.Clone() };
        staleFileRead.ExpectedRevision.Sequence++;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicFileLocations, SchematicFileLocations>(staleFileRead, token))).Status);
        Assert.AreEqual(before, await Capture());
        var parent = Symbol(before, root, new KIID { Value = fixture.First }).Clone();
        parent.Id.Value = Guid.NewGuid().ToString("D"); parent.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.Combine(fixture.Directory, "sheet-topology"));
        parent.NameField.Text.Text_ = "Added parent"; parent.FilenameField.Text.Text_ = "${KAICAD_FIXTURE_SHEET_ROOT}/adopted-parent.kicad_sch";
        parent.PageNumber = "10"; parent.InstanceRecords = null; parent.Variants = new(); parent.Path = root.SheetPath.Clone();
        await Native(true, new SchematicItemOperation { TargetDocument = root.Clone(), Create = Any.Pack(parent) });
        var parentDocument = Child(root, parent.Id);
        var withParent = await Publish("native-add-parent");
        Assert.IsNotNull(store.Read()!.State.NativeFileLocations);
        var parentFile = NativeSheetFileLocations.Find(store.Read()!.State, string.Join('/', parentDocument.SheetPath.Path.Select(p => p.Value)));
        Assert.IsNotNull(parentFile); Assert.IsTrue(parentFile.DeclarationIsAbsolute && parentFile.DeclarationMatchesLoaded);
        Assert.AreEqual(Path.Combine(fixture.Directory, "sheet-topology", "adopted-parent.kicad_sch"), parentFile.LoadedFilename);
        Guid parentModel = Binding(withParent, parentDocument).SheetInstanceId;
        Assert.AreEqual(original.Engineering.Circuit.SheetInstances.Count + 1, withParent.Engineering.Circuit.SheetInstances.Count);

        var nested = Symbol(await Capture(), root, parent.Id).Clone();
        nested.Id.Value = Guid.NewGuid().ToString("D"); nested.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        nested.NameField.Text.Text_ = "Added nested"; nested.FilenameField.Text.Text_ = "../${KAICAD_FIXTURE_SHEET_LEAF}";
        nested.PageNumber = "11"; nested.InstanceRecords = null; nested.Path = parentDocument.SheetPath.Clone();
        await Native(true, new SchematicItemOperation { TargetDocument = parentDocument.Clone(), Create = Any.Pack(nested) });
        var nestedDocument = Child(parentDocument, nested.Id);
        var withNested = await Publish("native-add-nested");
        Guid nestedModel = Binding(withNested, nestedDocument).SheetInstanceId;
        Assert.AreEqual(parentModel, withNested.Engineering.Circuit.SheetInstances.Single(s => s.Id == nestedModel).ParentId);

        var moved = Symbol(await Capture(), parentDocument, nested.Id).Clone();
        moved.Path = root.SheetPath.Clone(); moved.InstanceRecords = null;
        moved.FilenameField.Text.Text_ = "${KAICAD_FIXTURE_SHEET_LEAF}";
        await Native(true, new SchematicItemOperation { TargetDocument = parentDocument.Clone(), Remove = nested.Id.Clone() },
            new SchematicItemOperation { TargetDocument = root.Clone(), Create = Any.Pack(moved) });
        nestedDocument = Child(root, nested.Id);
        var relocated = await Publish("native-reparent");
        Assert.AreEqual(nestedModel, Binding(relocated, nestedDocument).SheetInstanceId, "Moving preserves the model identity.");
        Assert.AreEqual(Binding(relocated, root).SheetInstanceId, relocated.Engineering.Circuit.SheetInstances.Single(s => s.Id == nestedModel).ParentId);

        await Native(true, new SchematicItemOperation { TargetDocument = root.Clone(), Remove = nested.Id.Clone() });
        var removed = await Publish("native-remove");
        Assert.IsFalse(removed.Engineering.Circuit.SheetInstances.Any(s => s.Id == nestedModel));
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
        await FocusedSchematicShortcut(client, root, processId, display, "z", token);
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            while (!(await Capture()).Electrical.Hierarchy.Data.Instances.Any(s => s.Metadata.Document.Equals(nestedDocument)))
                await Task.Delay(50, limit.Token);
        }
        var undone = await Publish("native-remove-undo");
        Assert.AreEqual(nestedModel, Binding(undone, nestedDocument).SheetInstanceId, "Undo restores the original sheet identity.");

        var reparent = undone with { Engineering = undone.Engineering with { Circuit = undone.Engineering.Circuit with
        { SheetInstances = undone.Engineering.Circuit.SheetInstances.Select(s => s.Id == nestedModel ? s with { ParentId = parentModel } : s).ToArray() } } };
        var fromXml = await Xml(reparent, "xml-reparent");
        Assert.AreEqual("../${KAICAD_FIXTURE_SHEET_LEAF}", Symbol(await Capture(), parentDocument, nested.Id).FilenameField.Text.Text_);
        Assert.AreEqual(nestedModel, Binding(fromXml, Child(parentDocument, nested.Id)).SheetInstanceId);
        Assert.AreEqual(parentModel, fromXml.Engineering.Circuit.SheetInstances.Single(s => s.Id == nestedModel).ParentId);
        // Move one instance of the populated shared screen. Its other instance,
        // all component identities and both pin partitions must remain independent.
        var repeatedId = new KIID { Value = fixture.First };
        var repeatedDocument = Child(root, repeatedId);
        Guid repeatedModel = Binding(fromXml, repeatedDocument).SheetInstanceId;
        var repeatedOwners = fromXml.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == repeatedModel).ToArray();
        Assert.IsTrue(repeatedOwners.Length >= 3, "Use the populated repeated sheet, including the newly adopted probe.");
        var repeated = Symbol(await Capture(), root, repeatedId).Clone();
        repeated.Path = parentDocument.SheetPath.Clone(); repeated.InstanceRecords = null;
        repeated.FilenameField.Text.Text_ = "../shared-child.kicad_sch";
        await Native(false, new SchematicItemOperation { TargetDocument = root.Clone(), Remove = repeatedId.Clone() },
            new SchematicItemOperation { TargetDocument = parentDocument.Clone(), Create = Any.Pack(repeated) }, new SchematicItemOperation());
        await Native(true, new SchematicItemOperation { TargetDocument = root.Clone(), Remove = repeatedId.Clone() },
            new SchematicItemOperation { TargetDocument = parentDocument.Clone(), Create = Any.Pack(repeated) });
        var movedRepeated = await Publish("native-move-populated-repeat");
        Assert.AreEqual(repeatedModel, Binding(movedRepeated, Child(parentDocument, repeatedId)).SheetInstanceId);
        CollectionAssert.AreEquivalent(repeatedOwners,
            movedRepeated.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == repeatedModel).ToArray());
        var backToRoot = movedRepeated with { Engineering = movedRepeated.Engineering with { Circuit = movedRepeated.Engineering.Circuit with
        { SheetInstances = movedRepeated.Engineering.Circuit.SheetInstances.Select(s => s.Id == repeatedModel
            ? s with { ParentId = Binding(movedRepeated, root).SheetInstanceId } : s).ToArray() } } };
        fromXml = await Xml(backToRoot, "xml-move-populated-repeat");
        Assert.AreEqual(repeatedModel, Binding(fromXml, repeatedDocument).SheetInstanceId);
        CollectionAssert.AreEquivalent(repeatedOwners,
            fromXml.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == repeatedModel).ToArray());
        async Task<SchematicDesign> History(string key, DocumentSpecifier expected, bool exists, string phase)
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await Capture()).Electrical.Hierarchy.Data.Instances.Any(s => s.Metadata.Document.Equals(expected)) != exists)
                await Task.Delay(50, limit.Token);
            return await Publish(phase);
        }
        var xmlMoveUndone = await History("z", Child(parentDocument, repeatedId), true, "xml-move-undo");
        CollectionAssert.AreEquivalent(repeatedOwners,
            xmlMoveUndone.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == repeatedModel).ToArray());
        fromXml = await History("y", repeatedDocument, true, "xml-move-redo");
        CollectionAssert.AreEquivalent(repeatedOwners,
            fromXml.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == repeatedModel).ToArray());
        // Removing a branch unlinks its files; it does not destroy recoverable native data.
        var savedFiles = new[] { "sheet-topology/adopted-parent.kicad_sch", "adopted-nested.kicad_sch" }
            .ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(fixture.Directory, n)));
        var ids = new[] { parentModel, nestedModel }.ToHashSet();
        var definitions = fromXml.Engineering.Circuit.SheetInstances.Where(s => ids.Contains(s.Id)).Select(s => s.DefinitionId).ToHashSet();
        var delete = fromXml with { Engineering = fromXml.Engineering with { Circuit = fromXml.Engineering.Circuit with
        {
            SheetInstances = fromXml.Engineering.Circuit.SheetInstances.Where(s => !ids.Contains(s.Id)).ToArray(),
            Sheets = fromXml.Engineering.Circuit.Sheets.Where(s => !definitions.Contains(s.Id)).ToArray()
        } }, SheetBindings = fromXml.SheetBindings.Where(b => !ids.Contains(b.SheetInstanceId)).ToArray() };
        var deleted = await Xml(delete, "xml-remove-branch");
        Assert.IsFalse(deleted.Engineering.Circuit.SheetInstances.Any(s => ids.Contains(s.Id)));
        foreach (var (name, bytes) in savedFiles) CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, name), token));
        var removalUndone = await History("z", parentDocument, true, "xml-removal-undo");
        Assert.AreEqual(parentModel, Binding(removalUndone, parentDocument).SheetInstanceId);
        Assert.AreEqual(nestedModel, Binding(removalUndone, Child(parentDocument, nested.Id)).SheetInstanceId);
        var removalRedone = await History("y", parentDocument, false, "xml-removal-redo");
        CollectionAssert.AreEquivalent(deleted.Engineering.Circuit.SheetInstances.ToArray(), removalRedone.Engineering.Circuit.SheetInstances.ToArray());
        var settled = await Publish("settled");
        var saved = await Capture();
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var reloaded = await Capture();
        RequireToolSuccess(await call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = reloaded.State.Revision.Epoch }));
        var reopened = await Publish("saved-reload");
        CollectionAssert.AreEquivalent(settled.Engineering.Circuit.SheetInstances.ToArray(), reopened.Engineering.Circuit.SheetInstances.ToArray());
        CollectionAssert.AreEquivalent(settled.Engineering.Circuit.Components.ToArray(), reopened.Engineering.Circuit.Components.ToArray());
        Assert.AreEqual(saved.State.SaveStableStateSha256, (await Capture()).State.SaveStableStateSha256);
        // Add a third instance of the same physical file, with explicit native
        // placement records for its component references, through one real batch.
        var beforeShared = await Capture();
        var extra = Symbol(beforeShared, root, repeatedId).Clone();
        extra.Id.Value = Guid.NewGuid().ToString("D"); extra.NameField.Text.Text_ = "Third channel";
        extra.PageNumber = "12"; extra.InstanceRecords = null;
        var extraDocument = Child(root, extra.Id);
        var repeatedScreen = beforeShared.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(repeatedDocument));
        var creation = new List<SchematicItemOperation> { new() { TargetDocument = root.Clone(), Create = Any.Pack(extra) } };
        var physicalSymbols = repeatedScreen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).OrderBy(s => s.ReferenceField.Text.Text_, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < physicalSymbols.Length; ++i)
        {
            var instance = physicalSymbols[i].Clone(); instance.Path = extraDocument.SheetPath.Clone();
            instance.ReferenceField.Text.Text_ = "TP" + (301 + i);
            var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = instance.ReferenceField.Text.Text_,
                Unit = instance.Unit.Unit, Variants = instance.Variants?.Clone() ?? new() };
            record.Path.Add(extraDocument.SheetPath.Path.Select(p => p.Clone())); instance.InstanceRecords.Records.Add(record);
            creation.Add(new() { TargetDocument = extraDocument.Clone(), Update = Any.Pack(instance) });
        }
        await Native(true, creation.ToArray());
        RequireToolSuccess(await call("kicad_design_recovery_refresh", Recovery()));
        var needsReference = await call("kicad_design_sync_plan", Recovery());
        Assert.IsTrue(needsReference.GetProperty("isError").GetBoolean());
        var unresolved = needsReference.GetProperty("structuredContent");
        Assert.AreEqual("native_ownership_resolution_required", unresolved.GetProperty("errorCode").GetString());
        var question = unresolved.GetProperty("sheetComponentResolutionRequests").EnumerateArray().Single();
        byte[] unresolvedXml = await File.ReadAllBytesAsync(designPath, token);
        var unchangedNative = await Capture();
        string unresolvedToken = store.Read()!.RevisionToken;
        Guid answerSheet = question.GetProperty("SheetInstanceId").GetGuid(), answerDefinition = question.GetProperty("ComponentDefinitionId").GetGuid();
        object Choice(Guid definition, string reference, bool duplicate = false) => new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken,
            componentReferences = Enumerable.Repeat(new { sheetInstanceId = answerSheet, componentDefinitionId = definition, reference }, duplicate ? 2 : 1).ToArray(),
            symbolOwners = Array.Empty<object>() };
        var previousUndrawn = repeatedOwners.Single(c => !reopened.Engineering.Circuit.Symbols.Any(s => s.ComponentId == c.Id));
        foreach (var (invalid, code) in new[]
        {
            (Choice(Guid.NewGuid(), "TP350"), "native_repeated_sheet_answer_invalid"),
            (Choice(answerDefinition, "TP350", duplicate: true), "native_repeated_sheet_answer_invalid"),
            (Choice(answerDefinition, previousUndrawn.Reference), "native_addition_conflict")
        })
        {
            var refusal = await call("kicad_design_repeated_sheet_answer", invalid);
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean());
            Assert.AreEqual(code, refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            Assert.AreEqual(unresolvedToken, store.Read()!.RevisionToken);
            CollectionAssert.AreEqual(unresolvedXml, await File.ReadAllBytesAsync(designPath, token));
            Assert.AreEqual(unchangedNative, await Capture());
        }
        var answer = await call("kicad_design_repeated_sheet_answer", Choice(answerDefinition, "TP350"));
        RequireToolSuccess(answer);
        CollectionAssert.AreEqual(unresolvedXml, await File.ReadAllBytesAsync(designPath, token), "Retaining a choice does not publish partial XML.");
        Assert.AreEqual(unchangedNative, await Capture());
        // Even an edit subsequently undone advances native provenance. The old
        // choice cannot silently survive a different observed revision.
        var renamed = Symbol(await Capture(), root, extra.Id).Clone(); renamed.NameField.Text.Text_ += " temporary rename";
        await Native(true, new SchematicItemOperation { TargetDocument = root.Clone(), Update = Any.Pack(renamed) });
        await FocusedSchematicShortcut(client, root, processId, display, "z", token);
        using (var undoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            undoDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (Symbol(await Capture(), root, extra.Id).NameField.Text.Text_ != extra.NameField.Text.Text_)
                await Task.Delay(50, undoDeadline.Token);
        }
        RequireToolSuccess(await call("kicad_design_recovery_refresh", Recovery()));
        var staleChoice = await call("kicad_design_sync_plan", Recovery());
        Assert.AreEqual("native_ownership_resolution_required", staleChoice.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.HasCount(1, staleChoice.GetProperty("structuredContent").GetProperty("sheetComponentResolutionRequests").EnumerateArray());
        CollectionAssert.AreEqual(unresolvedXml, await File.ReadAllBytesAsync(designPath, token));
        RequireToolSuccess(await call("kicad_design_repeated_sheet_answer", Choice(answerDefinition, "TP350")));
        var sharedAdded = await Publish("native-add-repeated-sheet");
        Guid extraModel = Binding(sharedAdded, extraDocument).SheetInstanceId;
        Assert.AreEqual(reopened.Engineering.Circuit.SheetInstances.Single(s => s.Id == repeatedModel).DefinitionId,
            sharedAdded.Engineering.Circuit.SheetInstances.Single(s => s.Id == extraModel).DefinitionId);
        var extraOwners = sharedAdded.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == extraModel).ToArray();
        Assert.HasCount(4, extraOwners);
        CollectionAssert.AreEquivalent(new[] { "TP301", "TP302", "TP303", "TP350" }, extraOwners.Select(c => c.Reference).ToArray());
        CollectionAssert.AreEquivalent(repeatedOwners.Select(c => c.DefinitionId).ToArray(), extraOwners.Select(c => c.DefinitionId).ToArray());
        await Publish("native-add-repeated-settled");
        await History("z", extraDocument, false, "native-add-repeated-undo");
        var sharedRedone = await History("y", extraDocument, true, "native-add-repeated-redo");
        Assert.AreEqual(extraModel, Binding(sharedRedone, extraDocument).SheetInstanceId);
        CollectionAssert.AreEquivalent(extraOwners, sharedRedone.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == extraModel).ToArray());
        // The XML declares one more instance, its model owners, and exact native
        // placement records. No existing drawing or component is replaced.
        Guid xmlSheetId = Guid.NewGuid();
        var xmlNativeId = new KIID { Value = Guid.NewGuid().ToString("D") };
        var xmlDocument = Child(root, xmlNativeId);
        var xmlComponents = extraOwners.OrderBy(c => c.Reference, StringComparer.Ordinal).Select((c, i) => c with
            { Id = Guid.NewGuid(), SheetInstanceId = xmlSheetId, Reference = "TP" + (401 + i) }).ToArray();
        var ownerMap = extraOwners.OrderBy(c => c.Reference, StringComparer.Ordinal).Zip(xmlComponents).ToDictionary(p => p.First.Id, p => p.Second);
        var sourceOccurrences = sharedRedone.Engineering.Circuit.Symbols.Where(s => ownerMap.ContainsKey(s.ComponentId)).ToArray();
        var xmlOccurrences = sourceOccurrences.Select(s => s with { Id = Guid.NewGuid(), ComponentId = ownerMap[s.ComponentId].Id,
            SheetInstanceId = s.SheetInstanceId is null ? null : xmlSheetId }).ToArray();
        var extraBindings = sourceOccurrences.Zip(xmlOccurrences).Select(p => new SchematicSymbolBinding(p.Second.Id,
            sharedRedone.SymbolBindings.Single(b => b.SymbolOccurrenceId == p.First.Id).NativeObjectId)).ToArray();
        var referenceByNative = extraBindings.ToDictionary(b => b.NativeObjectId.ToString("D"), b => xmlComponents.Single(c =>
            c.Id == xmlOccurrences.Single(s => s.Id == b.SymbolOccurrenceId).ComponentId).Reference);
        var drawing = sharedRedone.Schematic.Clone();
        foreach (var screen in drawing.Instances.Where(s => s.Metadata.ScreenId.Equals(extra.ChildScreenId)))
            for (int i = 0; i < screen.Items.Count; ++i)
                if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
                {
                    var instance = screen.Items[i].Unpack<SchematicSymbolInstance>();
                    var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = referenceByNative[instance.Id.Value],
                        Unit = instance.Unit.Unit, Variants = instance.Variants?.Clone() ?? new() };
                    record.Path.Add(xmlDocument.SheetPath.Path.Select(p => p.Clone())); instance.InstanceRecords.Records.Add(record);
                    // Deliberately reverse native order: the planner must prepare
                    // the native representation without changing record values.
                    var ordered = instance.InstanceRecords.Records.OrderByDescending(r => string.Join('/', r.Path.Select(p => p.Value)), StringComparer.Ordinal).ToArray();
                    instance.InstanceRecords.Records.Clear(); instance.InstanceRecords.Records.Add(ordered);
                    screen.Items[i] = Any.Pack(instance);
                }
        var xmlScreen = drawing.Instances.Single(s => s.Metadata.Document.Equals(extraDocument)).Clone();
        xmlScreen.Metadata.Document = xmlDocument.Clone();
        for (int i = 0; i < xmlScreen.Items.Count; ++i)
            if (xmlScreen.Items[i].Is(SchematicSymbolInstance.Descriptor))
            {
                var instance = xmlScreen.Items[i].Unpack<SchematicSymbolInstance>();
                instance.Path = xmlDocument.SheetPath.Clone(); instance.ReferenceField.Text.Text_ = referenceByNative[instance.Id.Value];
                xmlScreen.Items[i] = Any.Pack(instance);
            }
        drawing.Instances.Add(xmlScreen);
        var xmlSheet = Symbol(await Capture(), root, extra.Id).Clone(); xmlSheet.Id = xmlNativeId.Clone();
        xmlSheet.NameField.Text.Text_ = "XML channel"; xmlSheet.PageNumber = "13";
        xmlSheet.Position.XNm += 50_800_000;
        foreach (var field in new[] { xmlSheet.NameField, xmlSheet.FilenameField }.Concat(xmlSheet.UserFields))
            if (field?.Text?.Position is { } position) position.XNm += 50_800_000;
        var xmlPlacement = new SheetPlacementRecord { ProjectName = root.Project.Name, PageNumber = "13", Variants = new() };
        xmlPlacement.Path.Add(root.SheetPath.Path.Select(p => p.Clone()));
        xmlSheet.InstanceRecords = new() { Records = { xmlPlacement } };
        drawing.Instances.Single(s => s.Metadata.Document.Equals(root)).Items.Add(Any.Pack(xmlSheet));
        var localNets = sharedRedone.Engineering.Circuit.Nets.Where(n => n.Pins.Count != 0 && n.Pins.All(p => ownerMap.ContainsKey(p.ComponentId)))
            .Select(n => n with { Id = Guid.NewGuid(), Name = n.Name + " XML channel",
                Pins = n.Pins.Select(p => p with { ComponentId = ownerMap[p.ComponentId].Id }).ToArray() }).ToArray();
        var xmlShared = sharedRedone with { Schematic = drawing, Engineering = sharedRedone.Engineering with
        { Circuit = sharedRedone.Engineering.Circuit with
        {
            SheetInstances = [.. sharedRedone.Engineering.Circuit.SheetInstances, new(xmlSheetId,
                sharedRedone.Engineering.Circuit.SheetInstances.Single(s => s.Id == extraModel).DefinitionId, Binding(sharedRedone, root).SheetInstanceId)],
            Components = [.. sharedRedone.Engineering.Circuit.Components, .. xmlComponents],
            Symbols = [.. sharedRedone.Engineering.Circuit.Symbols, .. xmlOccurrences],
            Nets = [.. sharedRedone.Engineering.Circuit.Nets, .. localNets]
        } }, SheetBindings = [.. sharedRedone.SheetBindings, new(xmlSheetId, xmlDocument.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())],
            SymbolBindings = [.. sharedRedone.SymbolBindings, .. extraBindings] };
        // These probes have the same part and value. Swapping their definition
        // identities must still fail before any native action or publication.
        var swapped = xmlShared with { Engineering = xmlShared.Engineering with { Circuit = xmlShared.Engineering.Circuit with
        { Components = xmlShared.Engineering.Circuit.Components.Select(c => c.Id == xmlComponents[0].Id
            ? c with { DefinitionId = xmlComponents[1].DefinitionId } : c.Id == xmlComponents[1].Id
                ? c with { DefinitionId = xmlComponents[0].DefinitionId } : c).ToArray() } } };
        string swappedXml = SchematicDesignXml.Write(swapped, []);
        await File.WriteAllTextAsync(designPath, swappedXml, new UTF8Encoding(false), token);
        RequireToolSuccess(await call("kicad_design_candidate_commit", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, candidateXml = swappedXml,
            expectedCandidateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(swappedXml))),
            operationId = Guid.NewGuid().ToString("D") }));
        var beforeRefusal = await Capture();
        var refusedSwap = await call("kicad_design_sync_plan", Recovery());
        Assert.IsTrue(refusedSwap.GetProperty("isError").GetBoolean());
        Assert.AreEqual("xml_shared_sheet_definition_mismatch", refusedSwap.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.AreEqual(beforeRefusal, await Capture());
        Assert.AreEqual(swappedXml, await File.ReadAllTextAsync(designPath, token));
        var xmlAdded = await Xml(xmlShared, "xml-add-repeated-sheet");
        Assert.AreEqual(xmlSheetId, Binding(xmlAdded, xmlDocument).SheetInstanceId);
        CollectionAssert.AreEquivalent(xmlComponents, xmlAdded.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == xmlSheetId).ToArray());
        await Publish("xml-add-repeated-settled");
        await History("z", xmlDocument, false, "xml-add-repeated-undo");
        var xmlRedone = await History("y", xmlDocument, true, "xml-add-repeated-redo");
        Assert.AreEqual(xmlSheetId, Binding(xmlRedone, xmlDocument).SheetInstanceId);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var finalNative = await Capture();
        RequireToolSuccess(await call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = finalNative.State.Revision.Epoch }));
        var finalModel = await Publish("shared-instances-reloaded");
        CollectionAssert.AreEquivalent(xmlComponents, finalModel.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == xmlSheetId).ToArray());
        CollectionAssert.AreEquivalent(extraOwners, finalModel.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == extraModel).ToArray());
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(designPath)!, "sheet-topology-proof.json"), JsonSerializer.Serialize(new
        { instanceId, nativeAdd = true, nativeMove = true, nativeRemove = true, nativeUndoRestoresIdentity = true,
            xmlMove = true, xmlRemove = true, populatedRepeatedMoveBothDirections = true,
            xmlUndoRedoPreservesIdentity = true, nativeRollbackPreservesRecords = true, savedReloadPreservesIdentity = true,
            nativeRepeatedInsertion = true, nativeRepeatedInsertionUndoRedo = true,
            xmlRepeatedInsertion = true, xmlRepeatedInsertionUndoRedo = true, repeatedInstancesReloaded = true,
            swappedDefinitionRefusedWithoutMutation = true, nativeRecordOrderPrepared = true,
            undrawnReferencePreserved = true, unknownOrDuplicateReferenceRefused = true, staleReferenceChoiceRefused = true,
            nativeVariablePathsPreserved = true, conflictingCompanionEnvironmentIgnored = true,
            fileLocationObservationReadOnly = true, staleFileLocationReadRefused = true,
            detachedFilesPreserved = true, publicSynchronization = true }), token);
    }
}
