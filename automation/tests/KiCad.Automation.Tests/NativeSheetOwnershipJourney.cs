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
        var parent = Symbol(before, root, new KIID { Value = fixture.First }).Clone();
        parent.Id.Value = Guid.NewGuid().ToString("D"); parent.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.Combine(fixture.Directory, "sheet-topology"));
        parent.NameField.Text.Text_ = "Added parent"; parent.FilenameField.Text.Text_ = "sheet-topology/adopted-parent.kicad_sch";
        parent.PageNumber = "10"; parent.InstanceRecords = null; parent.Variants = new(); parent.Path = root.SheetPath.Clone();
        await Native(true, new SchematicItemOperation { TargetDocument = root.Clone(), Create = Any.Pack(parent) });
        var parentDocument = Child(root, parent.Id);
        var withParent = await Publish("native-add-parent");
        Guid parentModel = Binding(withParent, parentDocument).SheetInstanceId;
        Assert.AreEqual(original.Engineering.Circuit.SheetInstances.Count + 1, withParent.Engineering.Circuit.SheetInstances.Count);

        var nested = Symbol(await Capture(), root, parent.Id).Clone();
        nested.Id.Value = Guid.NewGuid().ToString("D"); nested.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        nested.NameField.Text.Text_ = "Added nested"; nested.FilenameField.Text.Text_ = "../adopted-nested.kicad_sch";
        nested.PageNumber = "11"; nested.InstanceRecords = null; nested.Path = parentDocument.SheetPath.Clone();
        await Native(true, new SchematicItemOperation { TargetDocument = parentDocument.Clone(), Create = Any.Pack(nested) });
        var nestedDocument = Child(parentDocument, nested.Id);
        var withNested = await Publish("native-add-nested");
        Guid nestedModel = Binding(withNested, nestedDocument).SheetInstanceId;
        Assert.AreEqual(parentModel, withNested.Engineering.Circuit.SheetInstances.Single(s => s.Id == nestedModel).ParentId);

        var moved = Symbol(await Capture(), parentDocument, nested.Id).Clone();
        moved.Path = root.SheetPath.Clone(); moved.InstanceRecords = null;
        moved.FilenameField.Text.Text_ = "adopted-nested.kicad_sch";
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
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(designPath)!, "sheet-topology-proof.json"), JsonSerializer.Serialize(new
        { instanceId, nativeAdd = true, nativeMove = true, nativeRemove = true, nativeUndoRestoresIdentity = true,
            xmlMove = true, xmlRemove = true, populatedRepeatedMoveBothDirections = true,
            xmlUndoRedoPreservesIdentity = true, nativeRollbackPreservesRecords = true, savedReloadPreservesIdentity = true,
            detachedFilesPreserved = true, publicSynchronization = true }), token);
    }
}
