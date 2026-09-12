using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNetChainMetadata(NativeClient client, DocumentSpecifier root,
        string rootFile, ElectricalFixture fixture, int processId, string display, string evidence, CancellationToken token)
    {
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
        byte[] original = await File.ReadAllBytesAsync(rootFile, token);
        var query = new ReadSchematicHierarchyData { Document = root };
        var baseline = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token);
        var nets = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = root }, token);
        var net = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinA)
            && n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinB));
        var symbols = baseline.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).ToArray();
        string Reference(string pin) => symbols.Single(s => s.Definition.Items.Any(i =>
            i.Item.Is(SchematicPin.Descriptor) && i.Item.Unpack<SchematicPin>().Id.Value == pin)).ReferenceField.Text.Text_;
        string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string native = System.Text.Encoding.UTF8.GetString(original);
        int end = native.LastIndexOf(')'); Assert.IsTrue(end > 0);
        // Deliberately imported native fixture, not a production XML converter.
        // The source is disposable and restored byte-for-byte in the finally block.
        string definitions = $"""
            (net_chain "AUTOMATION_PATH" (from {Quote(Reference(fixture.PinA))} "1")
              (to {Quote(Reference(fixture.PinB))} "1") (net_class "Default")
              (color 51 102 153 0.5) (nets {Quote(net.Name)}))
            (net_chain "UNRESOLVED_PATH" (from "NOT_PRESENT" "1") (to "NOT_PRESENT_EITHER" "2")
              (net_class "UnknownClass") (nets "/UNRESOLVED"))
            (net_chain "PARTIAL_PATH" (from "NOT_PRESENT" "1"))
            (net_chain "NAMED_ONLY")
            """;
        Exception? primaryFailure = null;
        try
        {
            await File.WriteAllTextAsync(rootFile, native.Insert(end, definitions), token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
            var imported = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token);
            foreach (var screen in imported.Data.Instances)
            {
                var path = screen.Metadata.NetChains.Single(c => c.Name == "AUTOMATION_PATH");
                Assert.IsTrue(path.Committed, "The imported chain must enter the native committed-chain collection.");
                Assert.AreEqual(Reference(fixture.PinA), path.From.Reference); Assert.AreEqual("1", path.From.Pin);
                Assert.AreEqual(Reference(fixture.PinB), path.To.Reference); Assert.AreEqual("1", path.To.Pin);
                Assert.AreEqual("Default", path.NetClass);
                Assert.AreEqual(0.2, path.Color.R, 1e-6); Assert.AreEqual(0.4, path.Color.G, 1e-6);
                Assert.AreEqual(0.6, path.Color.B, 1e-6); Assert.AreEqual(0.5, path.Color.A, 1e-6);
                CollectionAssert.AreEqual(new[] { net.Name }, path.MemberNets.ToArray());
                var unresolved = screen.Metadata.NetChains.Single(c => c.Name == "UNRESOLVED_PATH");
                Assert.IsFalse(unresolved.Committed); Assert.AreEqual("NOT_PRESENT", unresolved.From.Reference);
                Assert.AreEqual("UnknownClass", unresolved.NetClass);
                CollectionAssert.AreEqual(new[] { "/UNRESOLVED" }, unresolved.MemberNets.ToArray());
                var partial = screen.Metadata.NetChains.Single(c => c.Name == "PARTIAL_PATH");
                Assert.IsFalse(partial.Committed);
                Assert.AreEqual("NOT_PRESENT", partial.From.Reference);
                Assert.AreEqual("1", partial.From.Pin);
                Assert.AreEqual("", partial.To.Reference);
                Assert.AreEqual("", partial.To.Pin);
                var namedOnly = screen.Metadata.NetChains.Single(c => c.Name == "NAMED_ONLY");
                Assert.IsFalse(namedOnly.Committed);
                Assert.AreEqual("", namedOnly.From.Reference);
                Assert.AreEqual("", namedOnly.To.Reference);
                Assert.AreEqual(0, namedOnly.MemberNets.Count);
                CollectionAssert.Contains(screen.Metadata.UnrepresentedState.ToArray(), "net_chains");
            }
            Assert.AreEqual(imported.Data, SchematicDataXml.Read(SchematicDataXml.Write(imported.Data)));
            Assert.AreEqual(imported, await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token));
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
            var saved = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token);
            foreach (var screen in saved.Data.Instances)
                Assert.IsTrue(imported.Data.Instances[0].Metadata.NetChains.Equals(screen.Metadata.NetChains),
                    "Saving and reloading must retain both committed and unresolved net-chain declarations. Observed: "
                    + string.Join(", ", screen.Metadata.NetChains.Select(c => c.Name)));

            await VerifyNetChainNameDialog(client, root, rootFile, fixture.PinA, processId, display, evidence, token);
            saved = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token);

            Task<SchematicHierarchyDataSnapshot> Read() =>
                client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token);
            string origin = Guid.NewGuid().ToString("D");
            ApplySchematicItemBatch Replace(SchematicHierarchyDataSnapshot before, SchematicNetChainState state)
            {
                var batch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = before.Revision,
                    DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), OriginId = origin };
                batch.Operations.Add(new SchematicItemOperation { ReplaceNetChains = state });
                return batch;
            }
            var replacement = new SchematicNetChainState();
            foreach (var chain in saved.Data.Instances[0].Metadata.NetChains)
            {
                var declaration = chain.Clone(); declaration.Committed = false;
                if (declaration.Name == "UNRESOLVED_PATH") declaration.NetClass = "PendingReview";
                replacement.Definitions.Add(declaration);
            }
            var edit = Replace(saved, replacement);
            var expected = saved.Data.Clone();
            foreach (var screen in expected.Instances)
                screen.Metadata.NetChains.Single(c => c.Name == "UNRESOLVED_PATH").NetClass = "PendingReview";
            edit.Operations.Clear();
            edit.Operations.Add(SchematicHierarchyDelta.Plan(saved.Data, expected));
            Assert.AreEqual(1, edit.Operations.Count, "All sheet copies share one native chain replacement.");
            var rejected = edit.Clone(); rejected.OperationId = Guid.NewGuid().ToString("D");
            rejected.Operations.Add(new SchematicItemOperation());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
            Assert.AreEqual(saved, await Read(), "Rejected chain replacement must restore the complete original state.");
            var invalid = edit.Clone(); invalid.OperationId = Guid.NewGuid().ToString("D");
            invalid.Operations[0].ReplaceNetChains.Definitions[0].Committed = true;
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalid, token));
            Assert.AreEqual(saved, await Read());
            foreach (Action<ApplySchematicItemBatch> corruptOrigin in new Action<ApplySchematicItemBatch>[]
            {
                b => b.OriginId = new string('x', 129), b => b.OriginId = "bad\0origin",
                b => b.ExpectedRevision = null,
                b => { b.OperationId = ""; b.DocumentEpoch = ""; }
            })
            {
                var malformed = edit.Clone(); malformed.OperationId = Guid.NewGuid().ToString("D");
                corruptOrigin(malformed);
                await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(malformed, token));
                Assert.AreEqual(saved, await Read());
            }
            foreach (Action<SchematicNetChainState> corrupt in new Action<SchematicNetChainState>[]
            {
                s => s.Definitions[0].Name = "bad name",
                s => s.Definitions[0].NetClass = "N\0UL",
                s => s.Definitions[0].From = new() { Reference = "U\0", Pin = "1" },
                s => s.Definitions[0].Color = new() { R = double.NaN },
                s => s.Definitions[0].Color = new() { A = 2 },
                s => s.Definitions[0].MemberNets.Add("__SG_runtime"),
                s => s.Definitions[0].MemberNets.Add(""),
                s => { s.Definitions[0].MemberNets.Add("/DUP"); s.Definitions[0].MemberNets.Add("/DUP"); },
                s => s.Definitions.Add(s.Definitions[0].Clone())
            })
            {
                var malformed = replacement.Clone(); corrupt(malformed);
                await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(Replace(saved, malformed), token));
                Assert.AreEqual(saved, await Read(), "Rejected native input must not alter state or revision.");
            }
            // Unknown field 127, varint value 1. Forward-version data must not
            // be silently dropped by the native replacement boundary.
            var future = SchematicNetChainState.Parser.ParseFrom(replacement.ToByteArray().Concat(new byte[] { 0xf8, 0x07, 0x01 }).ToArray());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(Replace(saved, future), token));
            Assert.AreEqual(saved, await Read());
            var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(edit, token);
            Assert.IsTrue(result.NetChainsChanged);
            var changed = await Read();
            Assert.AreEqual(expected, changed.Data);
            var events = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = saved.Revision.Epoch, AfterSequence = saved.Revision.Sequence }, token);
            Assert.AreEqual(1, events.Changes.Count, "Only the accepted commit may publish an attributed event.");
            Assert.AreEqual(origin, events.Changes.Single().OriginId);
            Assert.AreEqual(edit.OperationId, events.Changes.Single().OperationId);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(edit, token);
            Assert.AreEqual(changed, await Read(), "A retry must not duplicate the edit.");
            var noop = Replace(changed, replacement);
            Assert.IsFalse((await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(noop, token)).NetChainsChanged);
            Assert.AreEqual(changed, await Read());
            async Task History(string key, SchematicHierarchyData wanted)
            {
                var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                SchematicHierarchyDataSnapshot next;
                do
                {
                    next = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, deadline.Token);
                    if (next.Revision.Equals(before.Revision)) await Task.Delay(100, deadline.Token);
                } while (next.Revision.Equals(before.Revision));
                Assert.AreEqual(wanted, next.Data, "Chain undo/redo must preserve all other schematic state.");
                var history = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                    { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, deadline.Token);
                Assert.AreEqual(1, history.Changes.Count);
                Assert.AreEqual("", history.Changes.Single().OriginId, "User undo/redo must not inherit synchronization origin.");
                Assert.AreEqual("", history.Changes.Single().OperationId);
            }
            await History("z", saved.Data);
            await History("y", changed.Data);
            var beforeRemove = await Read();
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(Replace(beforeRemove, new()), token);
            var removed = await Read();
            foreach (var screen in removed.Data.Instances) Assert.AreEqual(0, screen.Metadata.NetChains.Count);
            await History("z", changed.Data);
            await History("z", saved.Data);
            var beforePersist = await Read();
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(Replace(beforePersist, replacement), token);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
            var persisted = await Read();
            foreach (var screen in persisted.Data.Instances)
                Assert.AreEqual(changed.Data.Instances[0].Metadata.NetChains, screen.Metadata.NetChains,
                    "An edited declaration must survive native save/reload, not only in-memory undo.");

            async Task ApplyPlanned(SchematicHierarchyDataSnapshot before, SchematicHierarchyData after)
            {
                var batch = Replace(before, new()); batch.Operations.Clear();
                batch.Operations.Add(SchematicHierarchyDelta.Plan(before.Data, after));
                await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            }
            var nativeDesired = persisted.Data.Clone();
            foreach (var screen in nativeDesired.Instances)
                screen.Metadata.NetChains.Single(c => c.Name == "PARTIAL_PATH").NetClass = "NativePreference";
            await ApplyPlanned(persisted, nativeDesired);
            var nativeVersion = await Read();
            var xmlDesired = persisted.Data.Clone();
            foreach (var screen in xmlDesired.Instances)
                screen.Metadata.NetChains.Single(c => c.Name == "UNRESOLVED_PATH").NetClass = "XmlPreference";
            var mergedHierarchy = nativeVersion.Data.Clone();
            foreach (var screen in mergedHierarchy.Instances)
            {
                var id = screen.Metadata.Document;
                var before = persisted.Data.Instances.Single(s => s.Metadata.Document.Equals(id));
                var fromXml = xmlDesired.Instances.Single(s => s.Metadata.Document.Equals(id));
                var fromNative = nativeVersion.Data.Instances.Single(s => s.Metadata.Document.Equals(id));
                var merge = SchematicItemMerge.Plan(before, fromXml, fromNative);
                Assert.IsTrue(merge.CanApply);
                screen.Metadata = merge.Merged!.Metadata.Clone();
            }
            await ApplyPlanned(nativeVersion, mergedHierarchy);
            var mergedNative = await Read();
            Assert.AreEqual(mergedHierarchy, mergedNative.Data);
            foreach (var screen in xmlDesired.Instances)
                screen.Metadata.NetChains.Single(c => c.Name == "PARTIAL_PATH").NetClass = "CompetingXmlPreference";
            var conflict = SchematicItemMerge.Plan(persisted.Data.Instances[0], xmlDesired.Instances[0], nativeVersion.Data.Instances[0]);
            Assert.IsFalse(conflict.CanApply);
            Assert.AreEqual(xmlDesired.Instances[0].Metadata, conflict.Conflicts.Single().Xml!.Unpack<SchematicMetadata>());
            Assert.AreEqual(nativeVersion.Data.Instances[0].Metadata, conflict.Conflicts.Single().Native!.Unpack<SchematicMetadata>());
            Assert.AreEqual(mergedNative, await Read(), "Conflict inspection must not mutate the live design.");
            var chosenHierarchy = mergedNative.Data.Clone();
            foreach (var screen in chosenHierarchy.Instances)
            {
                var id = screen.Metadata.Document;
                var resolution = SchematicItemMerge.Resolve(
                    persisted.Data.Instances.Single(s => s.Metadata.Document.Equals(id)),
                    xmlDesired.Instances.Single(s => s.Metadata.Document.Equals(id)),
                    mergedNative.Data.Instances.Single(s => s.Metadata.Document.Equals(id)),
                    new Dictionary<Guid, SchematicConflictChoice>(),
                    new Dictionary<string, SchematicConflictChoice> { ["PARTIAL_PATH"] = SchematicConflictChoice.Xml });
                Assert.IsTrue(resolution.CanApply);
                screen.Metadata = resolution.Merged!.Metadata.Clone();
            }
            await ApplyPlanned(mergedNative, chosenHierarchy);
            Assert.AreEqual(chosenHierarchy, (await Read()).Data);
            await History("z", mergedNative.Data);
            await History("z", nativeVersion.Data);
            await History("z", persisted.Data);
            await VerifyInferredNetChainCreation(client, root, rootFile, processId, display, evidence, token);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            await File.WriteAllTextAsync(Path.Combine(evidence, "chain-primary-failure.txt"), error.ToString(), CancellationToken.None);
            try { await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-primary-failure.png"), CancellationToken.None); }
            catch (Exception capture) { Console.Error.WriteLine("Chain failure capture: " + capture.Message); }
            throw;
        }
        finally
        {
            try
            {
                await File.WriteAllBytesAsync(rootFile, original, CancellationToken.None);
                // Restoration has its own finite allowance: a spent journey
                // token must not manufacture a second failure before trying.
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, cleanupDeadline.Token);
            }
            catch (Exception cleanup) when (primaryFailure is not null)
            {
                throw new AggregateException("Native chain journey and fixture restoration failed; both results are retained.", primaryFailure, cleanup);
            }
        }
        var restored = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(query, token);
        foreach (var screen in restored.Data.Instances)
            Assert.AreEqual(baseline.Data.Instances[0].Metadata.NetChains, screen.Metadata.NetChains);
    }
}
