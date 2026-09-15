using System.Text;
using System.Text.Json;
using Kiapi.Common.Types;
using Kiapi.Common.Commands;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySynchronizationServiceRestart(NativeClient client, DocumentSpecifier document,
        DesignRecoveryStore store, string designPath, string evidence, string instanceId, CancellationToken token)
    {
        string hostState = Path.Combine(evidence, instanceId + "-sync-host-state");
        var results = new List<object>();
        foreach (string stage in new[] { "native-edit", "native-save", "completed", "publication-staged",
                     "publication-replaced", "baseline-committed", "receipt-archived", "retained-archived" })
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            Console.WriteLine($"Native synchronization {instanceId}: terminate service at {stage}");
            var saved = store.Read()!;
            var desired = saved.State.Baseline with { Schematic = saved.State.Baseline.Schematic.Clone() };
            string title = "Service restart: " + stage;
            bool fileBoundary = stage is not ("native-edit" or "native-save" or "completed");
            if (fileBoundary)
            {
                // Native-only input requires a real XML replacement rather
                // than converging with an XML edit already saved by the caller.
                await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
                    { Document = document.Clone(), TitleBlock = new() { Title = title } }, limit.Token);
                saved = await DesignRecoveryInspector.RefreshAsync(store, client, saved.RevisionToken, limit.Token, includeElectrical: true);
            }
            else
            {
                desired.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.TitleBlock.Title = title;
                byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
                await File.WriteAllBytesAsync(designPath, xml, limit.Token);
                saved = store.Save(saved.State with { DesiredFileBytes = xml }, saved.RevisionToken);
            }
            Guid operationId = Guid.NewGuid();
            var arguments = new { instanceId, recoveryPath = store.StatePath, designPath,
                expectedRevisionToken = saved.RevisionToken, operationId = operationId.ToString("D") };
            string marker = Path.Combine(evidence, instanceId + "-" + stage + "-pause.json");
            CheckedSchematicState beforeStop;
            await using (var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(stage, marker), hostState,
                Path.Combine(evidence, instanceId + "-" + stage + "-first-host.log"), limit.Token))
            {
                RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                Task markerReady = SyncHarnessProcessTests.WaitForMarkerAsync(marker, limit.Token);
                Task<JsonElement> call = host.Tool("kicad_design_sync_apply", arguments);
                try
                {
                    await markerReady;
                    using var markerData = JsonDocument.Parse(await File.ReadAllBytesAsync(marker, limit.Token));
                    Assert.AreEqual(stage, markerData.RootElement.GetProperty("stage").GetString());
                    beforeStop = await Capture();
                    Assert.AreEqual(title, beforeStop.Electrical.Hierarchy.Data.Instances
                        .Single(s => s.Metadata.Document.Equals(document)).Metadata.TitleBlock.Title);
                    await host.TerminateAsync();
                    Assert.IsTrue(host.ForcedTermination);
                    await Assert.ThrowsAsync<IOException>(async () => { await call; });
                }
                catch
                {
                    await host.TerminateAsync();
                    try { await call; } catch (Exception) { }
                    throw;
                }
            }
            var alive = await client.HandshakeAsync(limit.Token);
            Assert.AreEqual(instanceId, alive.InstanceId);
            Assert.AreEqual(beforeStop.State.ProcessEpoch, alive.Epoch);
            var afterStop = await Capture();
            Assert.AreEqual(beforeStop.State, afterStop.State, "Stopping the service cannot close, save or alter its independent native editor.");
            if (stage == "native-edit") Assert.IsTrue(afterStop.State.NativeContentDirty, "Dirty editor work must survive the service process.");

            var recovery = new DesignRecoveryStore(store.StatePath).Read()!;
            bool completedBeforeStop = stage is "completed" or "baseline-committed" or "receipt-archived" or "retained-archived";
            if (completedBeforeStop) Assert.AreEqual(operationId, recovery.State.LastSynchronization!.OperationId);
            else Assert.AreEqual(operationId, recovery.State.PendingPublication!.OperationId);

            await using (var restarted = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(), hostState,
                Path.Combine(evidence, instanceId + "-" + stage + "-restarted-host.log"), limit.Token))
            {
                RequireToolSuccess(await restarted.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
                var result = await restarted.Tool("kicad_design_sync_apply", arguments);
                RequireToolSuccess(result);
                var data = result.GetProperty("structuredContent");
                Assert.AreEqual(operationId.ToString("D"), data.GetProperty("operationId").GetString());
                Assert.IsTrue(data.GetProperty("synchronizationCommitted").GetBoolean());
                Assert.AreEqual(completedBeforeStop, data.GetProperty("replayed").GetBoolean());
            }
            var final = await Capture();
            Assert.AreEqual(beforeStop.State.Revision, final.State.Revision, "Service restart cannot duplicate the committed native edit.");
            var recoveredXml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, limit.Token), []);
            Assert.AreEqual(0, SchematicHierarchyDelta.Plan(final.Electrical.Hierarchy.Data, recoveredXml.Schematic, limit.Token).Count);
            Assert.IsTrue(SchematicElectricalComparison.Compare(recoveredXml, final.Electrical, [], limit.Token).ConnectivityEquivalent);
            Assert.IsFalse(store.Read()!.State.HasPendingWork);
            results.Add(new { stage, operationId, nativeEpochPreserved = true, sameRevisionAfterRestart = true,
                sourceAndNativeAgree = true, actualServiceProcessTerminated = true });

            Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, limit.Token);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-sync-service-restart.json"), JsonSerializer.Serialize(new
        {
            instanceId, cases = results, testOnlyHost = true, productionToolAdvertised = false,
            automaticEventLoopQualified = false, crossPlatformReady = false
        }), token);
    }
}
