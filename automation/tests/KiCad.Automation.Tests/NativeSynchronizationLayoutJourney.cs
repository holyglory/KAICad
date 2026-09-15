using System.Text;
using System.Text.Json;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySynchronizationLayout(NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, string designPath, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        string hostState = Path.Combine(evidence, instanceId + "-layout-host-state");
        var results = new List<object>();
        foreach (string stage in new[] { "layout-prepared", "native-edit", "layout-resolved", "native-save", "publication-replaced", "completed" })
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            Console.WriteLine($"Native synchronization {instanceId}: connected XML placement interrupted at {stage}");
            var saved = store.Read()!; var baseline = saved.State.Baseline;
            var before = await Capture();
            var first = baseline.Engineering.Circuit.Symbols[0];
            var old = SchematicModelProjection.Placement(SchematicModelProjection.NativeSymbols(baseline, before.Electrical.Hierarchy.Data)[first.Id]);
            var wanted = old with { XMillimeters = old.XMillimeters + 2.54m, YMillimeters = old.YMillimeters + 2.54m };
            var desired = baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
            {
                Symbols = baseline.Engineering.Circuit.Symbols.Select(s => s.Id == first.Id ? s with { Placement = wanted } : s).ToArray(),
                Components = baseline.Engineering.Circuit.Components.Select(c => stage == "layout-prepared" && c.Id == first.ComponentId
                    ? c with { Reference = "TP990" } : c).ToArray()
            } } };
            byte[] desiredBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
            await File.WriteAllBytesAsync(designPath, desiredBytes, limit.Token);
            saved = store.Save(saved.State with { DesiredFileBytes = desiredBytes }, saved.RevisionToken);
            Guid operationId = Guid.NewGuid();
            var arguments = new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = saved.RevisionToken, operationId = operationId.ToString("D") };
            string marker = Path.Combine(evidence, instanceId + "-layout-" + stage + "-pause.json");
            CheckedSchematicState stopped;
            await using (var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(stage, marker), hostState,
                Path.Combine(evidence, instanceId + "-layout-" + stage + "-first-host.log"), limit.Token))
            {
                RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                Task markerReady = SyncHarnessProcessTests.WaitForMarkerAsync(marker, limit.Token);
                Task<JsonElement> call = host.Tool("kicad_design_sync_apply", arguments);
                try
                {
                    // A returned error is a real failed prerequisite, not a
                    // reason to spend the whole deadline waiting for a marker.
                    if (await Task.WhenAny(markerReady, call) == call)
                    { RequireToolSuccess(await call); Assert.Fail("The host completed without reaching the requested interruption."); }
                    await markerReady;
                    stopped = await Capture();
                    Assert.AreEqual(stage == "layout-prepared" ? old : wanted,
                        SchematicModelProjection.Placement(SchematicModelProjection.NativeSymbols(baseline, stopped.Electrical.Hierarchy.Data)[first.Id]));
                    await host.TerminateAsync(); Assert.IsTrue(host.ForcedTermination);
                    await Assert.ThrowsAsync<IOException>(async () => { await call; });
                }
                catch
                {
                    await host.TerminateAsync();
                    try { await call; } catch (Exception) { }
                    throw;
                }
            }
            Assert.AreEqual(stopped, await Capture(), "Stopping MCP must not close or change the independent editor.");
            var recovered = new DesignRecoveryStore(store.StatePath).Read()!;
            if (stage is "layout-prepared" or "native-edit")
            {
                Assert.AreEqual(operationId, recovered.State.PendingLayout!.OperationId);
                Assert.IsNull(recovered.State.PendingPublication); Assert.IsNull(recovered.State.PendingNativeSave);
                byte[] xmlBefore = await File.ReadAllBytesAsync(designPath, limit.Token);
                await File.WriteAllBytesAsync(designPath, "<newer-layout-input/>"u8.ToArray(), limit.Token);
                var audit = new SynchronizationRequestAudit();
                var rejected = await Assert.ThrowsExactlyAsync<AutomationException>(() => SchematicSynchronizationExecutor.ApplyAsync(
                    new(store.StatePath), new NativeClient(audit, client.Endpoint, client.Epoch), designPath, saved.RevisionToken, operationId, limit.Token));
                Assert.AreEqual("publication_target_changed", rejected.Code); Assert.AreEqual(0, audit.Mutations);
                Assert.AreEqual(recovered.RevisionToken, store.Read()!.RevisionToken);
                await File.WriteAllBytesAsync(designPath, xmlBefore, limit.Token);
            }
            else if (stage != "completed") Assert.AreEqual(operationId, recovered.State.PendingPublication!.OperationId);

            await using (var restarted = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(), hostState,
                Path.Combine(evidence, instanceId + "-layout-" + stage + "-restarted-host.log"), limit.Token))
            {
                RequireToolSuccess(await restarted.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                var result = await restarted.Tool("kicad_design_sync_apply", arguments);
                RequireToolSuccess(result);
                var data = result.GetProperty("structuredContent");
                Assert.IsTrue(data.GetProperty("synchronizationCommitted").GetBoolean());
                Assert.AreEqual(operationId.ToString("D"), data.GetProperty("operationId").GetString());
                Assert.AreEqual(stage == "completed", data.GetProperty("replayed").GetBoolean());
            }
            var final = await Capture();
            Assert.AreEqual(before.State.ProcessEpoch, final.State.ProcessEpoch);
            if (stage != "layout-prepared") Assert.AreEqual(stopped.State.Revision, final.State.Revision, "Restart must not repeat the move.");
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, limit.Token), []);
            Assert.AreEqual(wanted, xml.Engineering.Circuit.Symbols.Single(s => s.Id == first.Id).Placement);
            Assert.AreEqual(wanted, SchematicModelProjection.Placement(SchematicModelProjection.NativeSymbols(xml, final.Electrical.Hierarchy.Data)[first.Id]));
            Assert.AreEqual(0, SchematicHierarchyDelta.Plan(final.Electrical.Hierarchy.Data, xml.Schematic, limit.Token).Count);
            Assert.IsTrue(SchematicElectricalComparison.Compare(xml, final.Electrical, [], limit.Token).ConnectivityEquivalent);
            Assert.IsFalse(store.Read()!.State.HasPendingWork);
            var unchanged = store.Read()!;
            var noOp = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, unchanged.RevisionToken, Guid.NewGuid(), limit.Token);
            Assert.AreEqual(unchanged.RevisionToken, noOp.RecoveryRevisionToken); Assert.IsFalse(noOp.NativeMutationCommitted); Assert.IsFalse(noOp.NativeFilesSaved);
            results.Add(new { stage, operationId, requestedPlacementReached = true, sameNativeEpoch = true,
                actualWireGeometryCaptured = true, connectivityPreserved = true, duplicateMovementPrevented = true });

            Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, limit.Token);
        }
        // Reverse sync after native keyboard undo and redo must not create its
        // own move, replace native history, or disconnect the fixture pins.
        var last = store.Read()!.State.Baseline;
        foreach (string key in new[] { "z", "y" })
        {
            var before = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document.Clone(), ProcessEpoch = client.Epoch }, limit.Token)).State.Revision.Equals(before.State.Revision))
                await Task.Delay(50, limit.Token);
            var current = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
            var reverse = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, current.RevisionToken, Guid.NewGuid(), token);
            Assert.IsFalse(reverse.NativeMutationCommitted);
            var actual = store.Read()!.State;
            Assert.IsTrue(SchematicElectricalComparison.Compare(actual.Baseline, actual.ObservedElectrical!, [], token).ConnectivityEquivalent);
        }
        Assert.AreEqual(SchematicDesignXml.Write(last, []), SchematicDesignXml.Write(store.Read()!.State.Baseline, []));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-sync-layout.json"), JsonSerializer.Serialize(new
        { instanceId, cases = results, nativeKeyboardUndoRedo = true, testOnlyHost = true,
            productionToolAdvertised = false, rotationMirroringQualified = false, crossPlatformReady = false }), token);
    }
}
