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
    // Extend the real repeated-screen recovery fixture: one new physical symbol
    // becomes a separate component on both paths through the public sync tools.
    private static async Task VerifyRepeatedNativeAdoption(NativeClient client, DocumentSpecifier root,
        HierarchyFixture fixture, int processId, string display, string instanceId, string designPath,
        DesignRecoveryStore store, Func<string, object, Task<JsonElement>> call, CancellationToken token)
    {
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };
        async Task ReattachReloadedDocument()
        {
            var current = await Capture();
            RequireToolSuccess(await call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = current.State.Revision.Epoch }));
        }
        async Task<SchematicDesign> Publish()
        {
            RequireToolSuccess(await call("kicad_design_recovery_refresh", Recovery()));
            var plan = await call("kicad_design_sync_plan", Recovery());
            RequireToolSuccess(plan);
            Assert.AreEqual(0, plan.GetProperty("structuredContent").GetProperty("nativeOperationsJson").GetArrayLength(),
                "Adopting a native edit must not issue another native edit.");
            var applied = await call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(applied);
            Assert.IsFalse(applied.GetProperty("structuredContent").GetProperty("nativeMutationCommitted").GetBoolean());
            var written = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            var stored = store.Read()!.State;
            CollectionAssert.AreEqual(stored.DesiredFileBytes, await File.ReadAllBytesAsync(designPath, token),
                "The published bytes are exactly the recorded desired version.");
            string ByItemIdentity(SchematicDesign design)
            {
                var snapshot = design.Schematic.Clone();
                foreach (var screen in snapshot.Instances)
                {
                    // Native screen enumeration can reorder independent UUID-addressed
                    // items after save. Keep all item bytes, metadata and bindings exact.
                    var items = SchematicItemDelta.Index(screen.Items).OrderBy(p => p.Key).Select(p => Any.Pack(p.Value)).ToArray();
                    screen.Items.Clear(); screen.Items.Add(items);
                }
                return SchematicDesignXml.Write(design with { Schematic = snapshot }, []);
            }
            static string Digest(string xml) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml)));
            Assert.AreEqual(Digest(ByItemIdentity(stored.Baseline)), Digest(ByItemIdentity(written)),
                "The recorded baseline and published model keep every value and exact native item; only item enumeration may differ.");
            return written;
        }
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
        await ReattachReloadedDocument();
        var baseline = await Publish();
        var before = await Capture();
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = fixture.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = fixture.Second });
        var symbol = before.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(first)).Items
            .First(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        Guid nativeId = Guid.NewGuid(); symbol.Id.Value = nativeId.ToString("D"); symbol.Path = first.SheetPath.Clone();
        long dx = 150_000_000 - symbol.Position.XNm, dy = 150_000_000 - symbol.Position.YNm;
        foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField,
                     symbol.DatasheetField, symbol.DescriptionField }.Concat(symbol.UserFields))
            if (field?.Text?.Position is { } position) { position.XNm += dx; position.YNm += dy; }
        symbol.Position = new() { XNm = 150_000_000, YNm = 150_000_000 };
        symbol.ReferenceField.Text.Text_ = "TP103";
        symbol.InstanceRecords = new();
        foreach (var (document, reference) in new[] { (first, "TP103"), (second, "TP203") })
        {
            var record = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = reference, Unit = 1,
                Variants = new SchematicSymbolVariants() };
            record.Path.Add(document.SheetPath.Path.Select(p => p.Clone())); symbol.InstanceRecords.Records.Add(record);
        }
        foreach (var child in symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id.Value = Guid.NewGuid().ToString("D");
            child.Item = Any.Pack(pin);
        }
        var batch = new CheckedSchematicBatch { ExpectedState = before.State.Clone(), Batch = new()
        {
            Document = root.Clone(), DocumentEpoch = before.State.Revision.Epoch, ExpectedRevision = before.State.Revision.Clone(),
            OperationId = Guid.NewGuid().ToString("D"), Description = "Place one symbol on both repeated instances"
        } };
        batch.Batch.Operations.Add(new SchematicItemOperation { TargetDocument = first.Clone(), Create = Any.Pack(symbol) });
        var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(batch, token);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, receipt.ErrorMessage);
        var placed = await Capture();
        var adopted = await Publish();
        var owners = adopted.Engineering.Circuit.Components.Where(c => !baseline.Engineering.Circuit.Components.Any(b => b.Id == c.Id)).ToArray();
        Assert.HasCount(2, owners);
        CollectionAssert.AreEquivalent(new[] { "TP103", "TP203" }, owners.Select(c => c.Reference).ToArray());
        Assert.HasCount(1, owners.Select(c => c.DefinitionId).Distinct());
        Assert.HasCount(2, owners.Select(c => c.SheetInstanceId).Distinct());
        var occurrences = adopted.Engineering.Circuit.Symbols.Where(s => owners.Any(c => c.Id == s.ComponentId)).ToArray();
        Assert.HasCount(2, occurrences);
        Assert.IsTrue(adopted.SymbolBindings.Where(b => occurrences.Any(s => s.Id == b.SymbolOccurrenceId)).All(b => b.NativeObjectId == nativeId));
        foreach (var component in owners)
        {
            var path = adopted.SheetBindings.Single(b => b.SheetInstanceId == component.SheetInstanceId).NativePath;
            Assert.AreEqual(SchematicNativeAdditionProjection.AdoptedIdentity("component", adopted.Engineering.Circuit.Id, path, nativeId), component.Id);
            Assert.AreEqual(SchematicNativeAdditionProjection.AdoptedIdentity("occurrence", adopted.Engineering.Circuit.Id, path, nativeId),
                occurrences.Single(s => s.ComponentId == component.Id).Id);
        }
        var compared = SchematicElectricalComparison.Compare(adopted, (await Capture()).Electrical, []);
        Assert.IsTrue(compared.PinBindingsComplete && compared.ConnectivityEquivalent);
        Assert.AreEqual(placed.State.StateSha256, (await Capture()).State.StateSha256, "XML publication preserves the native edit.");
        byte[] published = await File.ReadAllBytesAsync(designPath, token);
        await Publish();
        CollectionAssert.AreEqual(published, await File.ReadAllBytesAsync(designPath, token), "Repeated synchronization is a no-op.");

        async Task<SchematicDesign> History(string key, bool present)
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = first.Clone() }, token);
            await FocusedSchematicShortcut(client, first, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var observed = await Capture();
                int count = observed.Electrical.Hierarchy.Data.Instances.SelectMany(s => s.Items)
                    .Count(i => i.Is(SchematicSymbolInstance.Descriptor) && i.Unpack<SchematicSymbolInstance>().Id.Value == nativeId.ToString("D"));
                if (count == (present ? 2 : 0)) break;
                await Task.Delay(50, limit.Token);
            }
            return await Publish();
        }
        var undone = await History("z", false);
        Assert.IsFalse(undone.Engineering.Circuit.Components.Any(c => owners.Any(o => o.Id == c.Id)));
        var redone = await History("y", true);
        CollectionAssert.AreEquivalent(owners, redone.Engineering.Circuit.Components.Where(c => owners.Any(o => o.Id == c.Id)).ToArray());
        CollectionAssert.AreEquivalent(occurrences, redone.Engineering.Circuit.Symbols.Where(s => occurrences.Any(o => o.Id == s.Id)).ToArray());
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root.Clone() }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        await ReattachReloadedDocument();
        var reopened = await Publish();
        CollectionAssert.AreEquivalent(owners, reopened.Engineering.Circuit.Components.Where(c => owners.Any(o => o.Id == c.Id)).ToArray());
        Assert.IsTrue(SchematicElectricalComparison.Compare(reopened, (await Capture()).Electrical, []).ConnectivityEquivalent);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(designPath)!, "adoption-proof.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeId, physicalSymbolsAdded = 1, componentInstancesAdded = 2, sharedDefinition = owners[0].DefinitionId,
            separateReferences = owners.Select(c => c.Reference), publicStdioPublication = true, nativeStatePreserved = true,
            repeatNoOp = true, undoRemovesBoth = true, redoRestoresExactIdentities = true, savedReloadPreservesBoth = true
        }), token);
    }
}
