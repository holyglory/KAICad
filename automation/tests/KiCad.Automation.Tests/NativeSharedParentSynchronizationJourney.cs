using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySharedParentXmlSynchronization(NativeClient client, DocumentSpecifier root,
        DocumentSpecifier childA, DocumentSpecifier childB, DocumentSpecifier unique, int processId, string display,
        string directory, string instanceId, Func<string, object, Task<JsonElement>> call, CancellationToken token)
    {
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        var initial = await Capture();
        var design = RepeatedProbeDesign(initial.Electrical, includeUndrawn: false);
        var initialComparison = SchematicElectricalComparison.Compare(design, initial.Electrical, [], token);
        Assert.IsTrue(initialComparison.PinBindingsComplete && initialComparison.ConnectivityEquivalent);
        var store = new DesignRecoveryStore(Path.Combine(directory, "shared-parent-sync.json"));
        string designPath = Path.Combine(directory, "shared-parent-design.xml");
        byte[] initialXml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, []));
        await File.WriteAllBytesAsync(designPath, initialXml, token);
        store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(initial.State.Revision.Epoch, initial.State.Revision.Sequence),
            initial.Electrical.Hierarchy.TrackingComplete, design, initialXml, initial.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone()), null);
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };
        Guid Sheet(SchematicDesign value, DocumentSpecifier document) => value.SheetBindings.Single(b =>
            b.NativePath.SequenceEqual(document.SheetPath.Path.Select(p => Guid.Parse(p.Value)))).SheetInstanceId;
        Guid a = Sheet(design, childA), b = Sheet(design, childB), rootId = Sheet(design, root);
        var retired = design.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == a).Select(c => c.Id).ToHashSet();
        SchematicDesign Collapse(Guid removed, Guid survivor)
        {
            var removedOwners = design.Engineering.Circuit.Components.Where(c => c.SheetInstanceId == removed).Select(c => c.Id).ToHashSet();
            var removedSymbols = design.Engineering.Circuit.Symbols.Where(s => removedOwners.Contains(s.ComponentId)).Select(s => s.Id).ToHashSet();
            return design with { Engineering = design.Engineering with { Circuit = design.Engineering.Circuit with
            {
                SheetInstances = design.Engineering.Circuit.SheetInstances.Where(s => s.Id != removed)
                    .Select(s => s.Id == survivor ? s with { ParentId = rootId } : s).ToArray(),
                Components = design.Engineering.Circuit.Components.Where(c => !removedOwners.Contains(c.Id)).ToArray(),
                Symbols = design.Engineering.Circuit.Symbols.Where(s => !removedSymbols.Contains(s.Id)).ToArray(),
                Nets = design.Engineering.Circuit.Nets.Select(n => n with { Pins = n.Pins.Where(p => !removedOwners.Contains(p.ComponentId)).ToArray() })
                    .Where(n => n.Pins.Count != 0).ToArray()
            } }, SheetBindings = design.SheetBindings.Where(s => s.SheetInstanceId != removed).ToArray(),
                SymbolBindings = design.SymbolBindings.Where(s => !removedSymbols.Contains(s.SymbolOccurrenceId)).ToArray() };
        }
        var collapsed = Collapse(a, b);
        async Task<SchematicDesign> Publish(string phase)
        {
            RequireToolSuccess(await call("kicad_design_recovery_refresh", Recovery()));
            var plan = await call("kicad_design_sync_plan", Recovery()); RequireToolSuccess(plan);
            var detail = plan.GetProperty("structuredContent");
            Assert.IsTrue(detail.GetProperty("canPrepare").GetBoolean(), phase + ": " + detail.GetRawText());
            var result = await call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D") });
            if (result.TryGetProperty("isError", out var failed) && failed.GetBoolean())
            {
                var state = await Capture();
                await File.WriteAllTextAsync(Path.Combine(directory, phase + "-failed-native.xml"), SchematicDataXml.Write(state.Electrical.Hierarchy.Data), token);
                if (store.Read()!.State.PendingPublication is { } pending)
                    await File.WriteAllBytesAsync(Path.Combine(directory, phase + "-failed-candidate.xml"), pending.CandidateFileBytes, token);
            }
            RequireToolSuccess(result);
            var published = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            var actual = await Capture(); var comparison = SchematicElectricalComparison.Compare(published, actual.Electrical, [], token);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, phase);
            return published;
        }
        async Task<SchematicDesign> Xml(SchematicDesign candidate, string phase)
        {
            string xml = SchematicDesignXml.Write(candidate, []); await File.WriteAllTextAsync(designPath, xml, new UTF8Encoding(false), token);
            RequireToolSuccess(await call("kicad_design_candidate_commit", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, candidateXml = xml,
                expectedCandidateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(xml))),
                operationId = Guid.NewGuid().ToString("D") }));
            return await Publish(phase);
        }
        var reduced = await Xml(collapsed, "shared-xml-collapse");
        Assert.AreEqual(b, Sheet(reduced, unique)); Assert.IsFalse(reduced.Engineering.Circuit.SheetInstances.Any(s => s.Id == a));
        foreach (var owner in design.Engineering.Circuit.Components.Where(c => !retired.Contains(c.Id)))
            Assert.AreEqual(owner, reduced.Engineering.Circuit.Components.Single(c => c.Id == owner.Id));
        var expanded = await Xml(design, "shared-xml-expansion");
        Assert.AreEqual(a, Sheet(expanded, childA)); Assert.AreEqual(b, Sheet(expanded, childB));
        CollectionAssert.AreEquivalent(design.Engineering.Circuit.Components.ToArray(), expanded.Engineering.Circuit.Components.ToArray());
        var answerStore = new DesignRecoveryStore(Path.Combine(directory, "shared-parent-answer.json"));
        string answerPath = Path.Combine(directory, "shared-parent-answer.xml");
        byte[] answerInput = await File.ReadAllBytesAsync(designPath, token);
        await File.WriteAllBytesAsync(answerPath, answerInput, token);
        answerStore.Save(store.Read()!.State with { OriginId = Guid.NewGuid(), LastSynchronization = null }, null);
        object AnswerRecovery() => new { instanceId, recoveryPath = answerStore.StatePath,
            expectedRevisionToken = answerStore.Read()!.RevisionToken };
        foreach (var key in new[] { "z", "y" })
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                    { Document = root.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).Electrical.Hierarchy.Data.Instances
                    .Any(s => s.Metadata.Document.Equals(childA)) != (key == "y")) await Task.Delay(50, limit.Token);
            }
            if (key == "z")
            {
                var actual = await Capture();
                var state = store.Read()!.State with { Observed = actual.Electrical.Hierarchy.Data.Clone(),
                    NativeRevision = new(actual.State.Revision.Epoch, actual.State.Revision.Sequence) };
                var history = await SchematicOwnershipHistoryReader.ReadAsync(store, state, token);
                Assert.IsNull(SchematicSheetMoveChoices.FromRemovalHistory(state, [], token));
                Assert.IsNotNull(SchematicSheetMoveChoices.FromRemovalHistory(state, history, token));
                var alternative = Collapse(b, a);
                alternative = alternative with { Schematic = state.Observed.Clone(),
                    SheetBindings = alternative.SheetBindings.Select(s => s.SheetInstanceId == a
                        ? s with { NativePath = unique.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray() } : s).ToArray() };
                Assert.IsNotNull(SchematicSheetMoveChoices.FromRemovalHistory(state,
                    [new(history[0].Receipt, alternative, false)], token), "Both survivor mappings are individually possible.");
                Assert.IsNull(SchematicSheetMoveChoices.FromRemovalHistory(state,
                    [.. history, new(history[0].Receipt, alternative, false)], token), "Conflicting historical identities must not be guessed.");

                // The same real native move without retained history needs the
                // user's explicit identity answer, then resumes through MCP.
                RequireToolSuccess(await call("kicad_design_recovery_refresh", AnswerRecovery()));
                var unanswered = await call("kicad_design_sync_plan", AnswerRecovery());
                Assert.IsTrue(unanswered.GetProperty("isError").GetBoolean());
                Assert.IsFalse(unanswered.GetProperty("structuredContent").GetProperty("canPrepare").GetBoolean());
                Assert.AreEqual(SchematicNativeSheetChanges.MoveAmbiguous,
                    unanswered.GetProperty("structuredContent").GetProperty("errorCode").GetString());
                var answered = await call(SchematicSheetMoveChoices.Tool, new { instanceId, recoveryPath = answerStore.StatePath,
                    expectedRevisionToken = answerStore.Read()!.RevisionToken,
                    moves = new[] { new SchematicSheetMoveAnswer(b, unique.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray()) },
                    retiredSheetInstanceIds = new[] { a }, addedNativePaths = Array.Empty<Guid[]>() });
                RequireToolSuccess(answered);
                CollectionAssert.AreEqual(answerInput, await File.ReadAllBytesAsync(answerPath, token));
                Assert.AreEqual(actual.Electrical.Hierarchy.Data, (await Capture()).Electrical.Hierarchy.Data);
                var answeredPlan = await call("kicad_design_sync_plan", AnswerRecovery()); RequireToolSuccess(answeredPlan);
                Assert.IsTrue(answeredPlan.GetProperty("structuredContent").GetProperty("canPrepare").GetBoolean(), answeredPlan.GetRawText());
                var appliedAnswer = await call("kicad_design_sync_apply", new { instanceId, recoveryPath = answerStore.StatePath,
                    designPath = answerPath, expectedRevisionToken = answerStore.Read()!.RevisionToken,
                    operationId = Guid.NewGuid().ToString("D") });
                RequireToolSuccess(appliedAnswer);
                Assert.IsFalse(appliedAnswer.GetProperty("structuredContent").GetProperty("nativeMutationCommitted").GetBoolean());
                var answeredDesign = SchematicDesignXml.Read(await File.ReadAllTextAsync(answerPath, token), []);
                Assert.AreEqual(b, Sheet(answeredDesign, unique));
                Assert.IsFalse(answeredDesign.Engineering.Circuit.SheetInstances.Any(s => s.Id == a));
                var answerComparison = SchematicElectricalComparison.Compare(answeredDesign, (await Capture()).Electrical, [], token);
                Assert.IsTrue(answerComparison.PinBindingsComplete && answerComparison.ConnectivityEquivalent);
                Assert.IsNull(answerStore.Read()!.State.SheetMoveResolution);
            }
            var changed = await Publish("shared-xml-" + key);
            if (key == "z") Assert.AreEqual(b, Sheet(changed, unique));
            else { Assert.AreEqual(a, Sheet(changed, childA)); Assert.AreEqual(b, Sheet(changed, childB)); }
        }
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var loaded = await Capture();
        RequireToolSuccess(await call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = loaded.State.Revision.Epoch }));
        var final = await Publish("shared-xml-reloaded");
        Assert.AreEqual(a, Sheet(final, childA)); Assert.AreEqual(b, Sheet(final, childB));
        byte[] beforeReplay = await File.ReadAllBytesAsync(designPath, token); await Publish("shared-xml-repeat");
        CollectionAssert.AreEqual(beforeReplay, await File.ReadAllBytesAsync(designPath, token));
        await File.WriteAllTextAsync(Path.Combine(directory, "shared-parent-xml-proof.json"), JsonSerializer.Serialize(new
        { xmlCollapse = true, xmlExpansion = true, existingIdentitiesPreserved = true, declaredAddedIdentitiesPreserved = true,
            nativeConnectionsMatched = true, historyRestoredIdentities = true, conflictingHistoryRefused = true,
            explicitNativeAnswerApplied = true, reloadExact = true, repeatNoOp = true }), token);
    }
}
