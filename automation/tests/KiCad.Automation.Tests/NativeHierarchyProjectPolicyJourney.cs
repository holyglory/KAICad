using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyHierarchyProjectPolicy(NativeClient client, DocumentSpecifier document,
        HierarchyFixture fixture, int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicHierarchyDataSnapshot> Read() => client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
            new() { Document = document }, token);
        string Prefix(string phase) => Path.Combine(evidence, processId + "-hierarchy-policy-" + phase);
        var failures = new List<Exception>();
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var baseline = await Read();
        Assert.IsNotNull(baseline.Data.Instances[0].Metadata.ErcSettings);
        Assert.IsNotEmpty(baseline.Data.Instances[0].Metadata.ErcSettings.RuleSeverities);
        Assert.IsNotNull(baseline.Data.Instances[0].Metadata.NetChainClasses);
        Assert.IsNotEmpty(baseline.Data.Instances[0].Metadata.NetChainClasses.Definitions);

        foreach (bool remove in new[] { false, true })
        foreach (bool nativeStructure in new[] { false, true })
        {
            string name = (remove ? "remove" : "insert") + (nativeStructure ? "-native" : "-xml");
            try
            {
                var before = await Read();
                await Same(baseline.Data, before.Data, name + "-initial");
                var structure = before.Data.Clone();
                if (remove) RemoveBranch(structure); else AddRepeatedBranch(structure);
                var policy = before.Data.Clone();
                foreach (var screen in policy.Instances)
                {
                    var severity = screen.Metadata.ErcSettings.RuleSeverities.First(x => x.Severity is RuleSeverity.RsError or RuleSeverity.RsWarning);
                    severity.Severity = severity.Severity == RuleSeverity.RsError ? RuleSeverity.RsWarning : RuleSeverity.RsError;
                    screen.Metadata.NetChainClasses.Definitions.Add("zz-hierarchy-policy-" + name);
                }
                var first = nativeStructure ? structure : policy;
                await Apply(before, SchematicHierarchyDelta.Plan(before.Data, first, token), name + "-independent-native-edit");
                var current = await Read();
                await Same(first, current.Data, name + "-native-input");
                var desiredXml = nativeStructure ? policy : structure;
                string xml = SchematicDataXml.Write(desiredXml);
                await File.WriteAllTextAsync(Prefix(name + "-desired.xml"), xml, token);
                var decoded = (SchematicHierarchyData)SchematicDataXml.Read(xml);
                var merge = SchematicHierarchyMerge.Plan(before.Data, decoded, current.Data, cancellationToken: token);
                Assert.IsTrue(merge.CanApply, merge.ErrorMessage);
                Assert.IsNotNull(merge.Merged);
                Assert.AreEqual(nativeStructure ? 1 : 0, merge.NativeOperations.Count(x => x.SetErcSettings is not null));
                Assert.AreEqual(nativeStructure ? 1 : 0, merge.NativeOperations.Count(x => x.ReplaceNetChainClasses is not null));
                foreach (var screen in merge.Merged.Instances)
                {
                    Assert.AreEqual(policy.Instances[0].Metadata.ErcSettings, screen.Metadata.ErcSettings);
                    Assert.AreEqual(policy.Instances[0].Metadata.NetChainClasses, screen.Metadata.NetChainClasses);
                }

                var rejected = Batch(current, merge.NativeOperations, name + "-rejected");
                rejected.Operations.Add(new SchematicItemOperation());
                await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
                Assert.AreEqual(current, await Read(), "A failed mixed hierarchy/project batch must leave no partial state.");
                await Apply(current, merge.NativeOperations, name + "-merged");
                var applied = await Read();
                await Same(merge.Merged, applied.Data, name + "-merged");
                Assert.AreEqual(0, SchematicHierarchyDelta.Plan(applied.Data, merge.Merged, token).Count);
                var converged = SchematicHierarchyMerge.Plan(before.Data, merge.Merged, applied.Data, cancellationToken: token);
                Assert.IsTrue(converged.CanApply, converged.ErrorMessage);
                Assert.AreEqual(0, converged.NativeOperations.Count, "Repeated synchronization must not create another edit.");

                await History("z", current.Data, name + "-undo");
                await History("y", merge.Merged, name + "-redo");
                var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                    new() { Document = document }, token);
                Assert.AreEqual(observation.Preview.Revision, observation.Snapshot.Revision);
                await File.WriteAllBytesAsync(Prefix(name + "-native.png"), observation.Preview.Png.ToByteArray(), token);
                await NativeKeyboard.CaptureAsync(display, Prefix(name + "-desktop.png"), token);

                // Competing changes to the same project rule remain a conflict even
                // while the other side inserts or removes a sheet.
                var competing = structure.Clone();
                foreach (var screen in competing.Instances)
                {
                    var changedRule = policy.Instances[0].Metadata.ErcSettings.RuleSeverities
                        .Select((rule, index) => (rule, index)).First(x => !x.rule.Equals(before.Data.Instances[0].Metadata.ErcSettings.RuleSeverities[x.index]));
                    screen.Metadata.ErcSettings.RuleSeverities[changedRule.index].Severity = RuleSeverity.RsIgnore;
                }
                Assert.IsFalse(SchematicHierarchyMerge.Plan(before.Data, competing, policy, cancellationToken: token).CanApply);
                await Same(applied.Data, (await Read()).Data, name + "-conflict-inspection-unchanged");

                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
                await Same(merge.Merged, (await Read()).Data, name + "-save-reopen");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(new InvalidOperationException(name, error));
                await File.WriteAllTextAsync(Prefix(name + "-failure.txt"), error.ToString(), token);
            }
            // Restore only this disposable fixture; a recovery failure prevents
            // later scenarios from being reported against a fabricated baseline.
            try
            {
                var current = await Read();
                await Apply(current, SchematicHierarchyDelta.Plan(current.Data, baseline.Data, token), name + "-restore");
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
                await Same(baseline.Data, (await Read()).Data, name + "-restored");
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(name + " recovery", error));
                throw new AggregateException(failures);
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);

        ApplySchematicItemBatch Batch(SchematicHierarchyDataSnapshot state, IEnumerable<SchematicItemOperation> operations, string name)
        {
            var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = state.Revision.Clone(),
                DocumentEpoch = state.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = name };
            batch.Operations.Add(operations.Select(x => x.Clone()));
            return batch;
        }
        async Task Apply(SchematicHierarchyDataSnapshot state, IEnumerable<SchematicItemOperation> operations, string name)
        {
            var batch = Batch(state, operations, name);
            if (batch.Operations.Count == 0) return;
            var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            Assert.AreEqual(result, await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token),
                "An identical operation retry must not duplicate a sheet or project edit.");
        }
        async Task Same(SchematicHierarchyData expected, SchematicHierarchyData actual, string name)
        {
            await File.WriteAllTextAsync(Prefix(name + "-actual.xml"), SchematicDataXml.Write(actual), token);
            var canonicalExpected = Canonical(expected); var canonicalActual = Canonical(actual);
            if (!canonicalExpected.Equals(canonicalActual))
            {
                await File.WriteAllTextAsync(Prefix(name + "-expected.xml"), SchematicDataXml.Write(expected), token);
                Assert.Fail(NativeSnapshotDifference.Describe(canonicalExpected, canonicalActual));
            }
            var expectedRoundTrip = actual.Clone();
            Assert.AreEqual(expectedRoundTrip, SchematicDataXml.Read(SchematicDataXml.Write(actual)));
        }
        static SchematicHierarchyData Canonical(SchematicHierarchyData value)
        {
            // Native snapshots enumerate identity-addressed collections in UUID
            // order. Their enumeration order is not persisted schematic geometry.
            // Keep every object, field, repeated property and document path intact.
            var result = value.Clone();
            var screens = result.Instances.OrderBy(x => string.Join('/', x.Metadata.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal).ToArray();
            result.Instances.Clear(); result.Instances.Add(screens);
            foreach (var screen in result.Instances)
            {
                var items = screen.Items.OrderBy(ItemId, StringComparer.Ordinal).ToArray();
                screen.Items.Clear(); screen.Items.Add(items);
            }
            return result;
        }
        static string ItemId(Any item)
        {
            var descriptor = SchematicText.Descriptor.File.MessageTypes.Single(x => item.Is(x));
            IMessage message = descriptor.Parser.ParseFrom(item.Value);
            return ((KIID)descriptor.FindFieldByName("id").Accessor.GetValue(message)).Value;
        }
        async Task History(string key, SchematicHierarchyData expected, string name)
        {
            var before = await Read();
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            int delay = 25;
            SchematicHierarchyDataSnapshot after;
            do
            {
                after = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document }, limit.Token);
                if (after.Revision.Equals(before.Revision)) { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            } while (after.Revision.Equals(before.Revision));
            await Same(expected, after.Data, name);
        }
        void RemoveBranch(SchematicHierarchyData data)
        {
            var root = data.Instances.Single(x => x.Metadata.Document.Equals(document));
            var item = root.Items.Single(x => x.Is(SheetSymbol.Descriptor) && x.Unpack<SheetSymbol>().Id.Value == fixture.First);
            root.Items.Remove(item);
            var child = data.Instances.Single(x => x.Metadata.Document.SheetPath.Path.Last().Value == fixture.First);
            data.Instances.Remove(child);
        }
        void AddRepeatedBranch(SchematicHierarchyData data)
        {
            var root = data.Instances.Single(x => x.Metadata.Document.Equals(document));
            var sheet = root.Items.Single(x => x.Is(SheetSymbol.Descriptor) && x.Unpack<SheetSymbol>().Id.Value == fixture.First).Unpack<SheetSymbol>();
            sheet.Id.Value = Guid.NewGuid().ToString("D");
            sheet.NameField.Text.Text_ = "Policy merge repeated sheet";
            sheet.PageNumber = "4";
            foreach (var instance in sheet.InstanceRecords.Records.Where(x => x.Path.SequenceEqual(document.SheetPath.Path))) instance.PageNumber = "4";
            sheet.Position.YNm += 50000000;
            root.Items.Add(Any.Pack(sheet));
            var original = data.Instances.First(x => x.Metadata.ScreenId.Equals(sheet.ChildScreenId));
            var repeated = original.Clone();
            repeated.Metadata.Document = document.Clone();
            repeated.Metadata.Document.SheetPath.Path.Add(sheet.Id.Clone());
            data.Instances.Add(repeated);
        }
    }
}
