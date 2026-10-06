using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static int simulationRestartProofClaimed;

    private static async Task VerifyNativeSimulation(NativeClient client, DocumentSpecifier document,
        string evidence, string instanceId, string display, InstanceRegistry registry,
        Action<Process> retainProcess, CancellationToken token)
    {
        // Exercise a real child path so an MCP binding that drops or truncates the
        // protobuf repeated UUID list cannot accidentally pass with a root target.
        var hierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
            new() { Document = document }, token);
        var simulationDocument = hierarchy.Data.Instances.Select(instance => instance.Metadata.Document)
            .First(target => target.SheetPath.Path.Count > 1).Clone();
        Assert.IsGreaterThan(1, simulationDocument.SheetPath.Path.Count);
        string stateDirectory = Directory.CreateTempSubdirectory("kicad-simulation-mcp-").FullName;
        try
        {
            var mcpStart = SyncHarnessProcessTests.ProductionStartInfo();
            // kicad_instance_start inherits the MCP host environment. Carry the
            // test-owned Xvfb display through so the restarted native editor can
            // initialize GTK just like the original fixture process.
            mcpStart.Environment["DISPLAY"] = display;
            mcpStart.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            mcpStart.Environment["XDG_CONFIG_HOME"] = Path.Combine(stateDirectory, "config");
            mcpStart.Environment["XDG_CACHE_HOME"] = Path.Combine(stateDirectory, "cache");
            await using var mcp = await StdioMcpFixture.StartAsync(mcpStart,
                stateDirectory, Path.Combine(evidence, instanceId + "-simulation-mcp.stderr.log"), token);
            var attached = await mcp.Tool("kicad_instance_attach", new
            { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean(), attached.GetRawText());

            const string netlist = "Automation divider\nV1 in 0 5\nR1 in out 1k\nR2 out 0 1k\n.op\n.end\n";
            string operation = Guid.NewGuid().ToString("D");
            var started = await Start(netlist, operation, numericType: true);
            Assert.AreEqual(SimulationJobStatus.SimjsRunning, started.Status);
            Assert.IsFalse(started.WorkerFinished);
            var current = await WaitForTerminal(started);
            Assert.AreEqual(SimulationJobStatus.SimjsCompleted, current.Status, current.ErrorMessage);
            Assert.IsTrue(current.WorkerFinished);
            Assert.IsTrue(current.Vectors.Any(vector => vector.Name.Contains("out", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(current.Vectors.SelectMany(vector => vector.Values).Any(value => Math.Abs(value - 2.5) < 0.01));
            Assert.IsTrue(current.Vectors.All(vector => !vector.Complex), "The operating-point vectors must retain their real, flat representation.");
            var nativeOperation = await client.InvokeAsync<NativeCapabilitySimulationReadOperation,
                NativeCapabilitySimulationReadOperationResponse>(new()
                { Document = simulationDocument, OperationId = operation, ProcessEpoch = client.Epoch }, token);
            Assert.IsTrue(nativeOperation.Found);
            Assert.IsNotNull(nativeOperation.Job);
            Assert.AreEqual(current.JobId, nativeOperation.Job.JobId);
            Assert.AreEqual(current.Status, nativeOperation.Job.Status);
            var operationLookup = await mcp.Tool("kicad_simulation_operation", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), operationId = operation });
            Assert.AreEqual(current.JobId, JobResult(operationLookup).JobId,
                "Operation lookup must recover the existing native job without dispatching a duplicate.");
            var missingOperationLookup = await mcp.Tool("kicad_simulation_operation", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), operationId = Guid.NewGuid().ToString("D") });
            Assert.IsFalse(missingOperationLookup.GetProperty("isError").GetBoolean());
            Assert.IsFalse(missingOperationLookup.GetProperty("structuredContent").GetProperty("found").GetBoolean(),
                "A native peer with no authoritative operation record must report found=false.");
            string originalEpoch = client.Epoch;
            var completedArchive = await ArchiveResult(mcp, current.JobId, originalEpoch);
            Assert.AreEqual(current, JobResult(completedArchive, originalEpoch));

            // Save through the checked lifecycle transaction, replay its receipt,
            // then reload the schematic. The native simulation result is a
            // process-owned receipt and must remain readable across that reload.
            var beforeReload = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = simulationDocument }, token);
            var saveRequest = new CheckedSaveDocument
            {
                Document = simulationDocument.Clone(),
                ExpectedState = beforeReload.Clone(),
                OperationId = Guid.NewGuid().ToString("D")
            };
            var saveReceipt = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(saveRequest, token);
            Assert.AreEqual(LifecycleOperationStatus.LosSaved, saveReceipt.Status, saveReceipt.ErrorMessage);
            Assert.AreEqual(saveReceipt,
                await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(saveRequest, token),
                "Replaying the checked save must return its original receipt without writing again.");
            await client.InvokeAsync<RevertDocument, Google.Protobuf.WellKnownTypes.Empty>(
                new() { Document = simulationDocument.Clone() }, token);
            var afterReload = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = simulationDocument, JobId = started.JobId, ProcessEpoch = client.Epoch }, token);
            Assert.AreEqual(current, afterReload,
                "Reloading the saved schematic must retain the completed native simulation result.");

            var completedRead = JobResult(await mcp.Tool("kicad_simulation_job", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = started.JobId }));
            Assert.AreEqual(SimulationJobStatus.SimjsCompleted, completedRead.Status);
            Assert.AreEqual(current, completedRead, "The MCP job read must retain the complete native result.");

            // Reopen the same private state directory in a fresh MCP process.
            // Neither vectors nor the original deck provenance may depend on
            // an in-memory map in the first host.
            {
                await using var reconnect = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                    stateDirectory, Path.Combine(evidence, instanceId + "-simulation-mcp-reconnect.stderr.log"), token);
                var reconnectArchive = await ArchiveResult(reconnect, current.JobId, originalEpoch);
                Assert.IsTrue(JsonElement.DeepEquals(completedArchive.GetProperty("structuredContent"),
                    reconnectArchive.GetProperty("structuredContent")), "A fresh MCP host must retain exact archived vectors and provenance.");
                var reconnectAttached = await reconnect.Tool("kicad_instance_attach", new
                    { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(reconnectAttached.TryGetProperty("isError", out var reconnectError)
                    && reconnectError.GetBoolean(), reconnectAttached.GetRawText());
                var reconnectRead = JobResult(await reconnect.Tool("kicad_simulation_job", new
                { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = started.JobId }));
                Assert.AreEqual(current, reconnectRead,
                    "A fresh MCP connection must recover the native result instead of inventing a new job.");

                // The durable operation fingerprint must reject changed bytes
                // after MCP restart before dispatching to the native editor.
                var reused = await reconnect.Tool("kicad_simulation_start", new
                {
                    instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument),
                    netlist = netlist + "* changed after reconnect\n", operationId = operation
                });
                AssertFailure(reused, "simulation_operation_reused");
            }

            var completedCancel = JobResult(await mcp.Tool("kicad_simulation_cancel", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = started.JobId }));
            Assert.AreEqual(current, completedCancel, "Cancelling a completed job must preserve its terminal result.");

            var nativeRead = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = simulationDocument, JobId = started.JobId, ProcessEpoch = client.Epoch }, token);
            Assert.AreEqual(current, nativeRead, "MCP must expose the same exact document and vectors retained by KiCad.");
            var inlineDependencies = await client.InvokeAsync<NativeCapabilitySimulationReadDependencies,
                NativeCapabilitySimulationDependencies>(new()
                { Document = simulationDocument, JobId = started.JobId, ProcessEpoch = client.Epoch }, token);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(netlist))),
                inlineDependencies.GeneratedDeckSha256, "Native dependency evidence must hash the exact deck bytes sent to ngspice.");
            var archivedDependencies = completedArchive.GetProperty("structuredContent").GetProperty("dependencies");
            Assert.AreEqual(inlineDependencies.GeneratedDeckSha256, archivedDependencies.GetProperty("generatedDeckSha256").GetString());
            Assert.AreEqual(inlineDependencies.DependencyCaptureComplete, archivedDependencies.GetProperty("dependencyCaptureComplete").GetBoolean());
            CollectionAssert.AreEqual(inlineDependencies.DependencyPaths.ToArray(),
                archivedDependencies.GetProperty("dependencyPaths").EnumerateArray().Select(path => path.GetString()).ToArray());
            CollectionAssert.AreEqual(inlineDependencies.Warnings.ToArray(),
                archivedDependencies.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray());

            var replay = await Start(netlist, operation);
            Assert.AreEqual(current, replay, "An identical operation replay must retain the same completed job and vectors.");
            var changedDeck = await mcp.Tool("kicad_simulation_start", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument),
                netlist = netlist + "* changed\n", operationId = operation
            });
            AssertFailure(changedDeck, "simulation_operation_reused");

            // The root is valid in this same project, but it is a different full
            // sheet path from the child bound to this job.
            foreach (string name in new[] { "kicad_simulation_job", "kicad_simulation_operation", "kicad_simulation_wait", "kicad_simulation_cancel" })
            {
                var arguments = new Dictionary<string, object>
                {
                    ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = client.Epoch,
                    ["document"] = Target(document),
                    [name == "kicad_simulation_operation" ? "operationId" : "jobId"] =
                        name == "kicad_simulation_operation" ? operation : started.JobId
                };
                if (name == "kicad_simulation_wait") arguments["afterSequence"] = current.Sequence;
                var wrongSheet = await mcp.Tool(name, arguments);
                AssertFailure(wrongSheet, name == "kicad_simulation_wait" ? "simulation_wait_incomplete" : "simulation_unavailable");
                arguments["document"] = Target(simulationDocument);
                arguments["expectedInstanceEpoch"] = Guid.NewGuid().ToString("D");
                AssertFailure(await mcp.Tool(name, arguments), "simulation_instance_changed");
            }
            var wrongProject = simulationDocument.Clone();
            wrongProject.Project.Path = Path.Combine(wrongProject.Project.Path, "different-project");
            AssertFailure(await mcp.Tool("kicad_simulation_job", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch,
                document = Target(wrongProject), jobId = started.JobId
            }), "simulation_unavailable");
            AssertFailure(await mcp.Tool("kicad_simulation_start", new
            {
                instanceId, expectedInstanceEpoch = Guid.NewGuid().ToString("D"),
                document = Target(simulationDocument), netlist, operationId = Guid.NewGuid().ToString("D")
            }), "simulation_instance_changed");

            var generated = await Start("", Guid.NewGuid().ToString("D"));
            var generatedTerminal = await WaitForTerminal(generated);
            Assert.IsTrue(generatedTerminal.Status is SimulationJobStatus.SimjsCompleted or SimulationJobStatus.SimjsFailed);
            await VerifyGeneratedSimulationDependenciesThroughMcp(client, mcp, simulationDocument,
                generatedTerminal, evidence, instanceId, token);
            var afterLaterJob = await ArchiveResult(mcp, current.JobId, originalEpoch);
            Assert.IsTrue(JsonElement.DeepEquals(completedArchive.GetProperty("structuredContent"),
                afterLaterJob.GetProperty("structuredContent")), "Starting a later native job must preserve the earlier archive.");
            var historicalOperation = await client.InvokeAsync<NativeCapabilitySimulationReadOperation,
                NativeCapabilitySimulationReadOperationResponse>(new()
                { Document = simulationDocument, OperationId = operation, ProcessEpoch = originalEpoch }, token);
            Assert.IsTrue(historicalOperation.Found);
            Assert.IsNotNull(historicalOperation.Job);
            Assert.AreEqual(current.JobId, historicalOperation.Job.JobId,
                "Native operation history must retain the original job after a later simulation.");
            Assert.AreEqual(current.Status, historicalOperation.Job.Status);
            Assert.AreEqual(current.JobId, JobResult(await mcp.Tool("kicad_simulation_operation", new
                { instanceId, expectedInstanceEpoch = originalEpoch, document = Target(simulationDocument), operationId = operation })).JobId,
                "Managed operation recovery must read the original job after a later simulation.");
            Assert.AreEqual(current, await Start(netlist, operation), "An old operation must replay its archive without running again.");
            Assert.AreEqual(generatedTerminal, await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = simulationDocument, JobId = generated.JobId, ProcessEpoch = originalEpoch }, token),
                "Replaying an archived operation must not replace the later native job.");
            foreach (var foreign in new[]
            {
                new { instanceId = instanceId + "-other", processEpoch = originalEpoch, document = Target(simulationDocument), jobId = current.JobId },
                new { instanceId, processEpoch = Guid.NewGuid().ToString("D"), document = Target(simulationDocument), jobId = current.JobId },
                new { instanceId, processEpoch = originalEpoch, document = Target(document), jobId = current.JobId }
            })
                AssertFailure(await mcp.Tool("kicad_simulation_result", foreign), "simulation_result_not_found");

            const string longNetlist = "Long transient\nV1 in 0 PULSE(0 5 0 1n 1n 1m 2m)\nR1 in out 1k\nC1 out 0 1u\n.tran 1u 1000m\n.end\n";
            var longRun = await Start(longNetlist, Guid.NewGuid().ToString("D"));
            var cancelled = JobResult(await mcp.Tool("kicad_simulation_cancel", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument, numericType: true), jobId = longRun.JobId }));
            Assert.AreEqual(SimulationJobStatus.SimjsCancelled, cancelled.Status);
            Assert.IsTrue(cancelled.CancellationRequested);
            Assert.IsTrue(cancelled.WorkerFinished);
            Assert.IsEmpty(cancelled.Vectors);
            var cancelledRead = JobResult(await mcp.Tool("kicad_simulation_job", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = longRun.JobId }));
            Assert.AreEqual(cancelled, cancelledRead);
            var cancelledArchive = await ArchiveResult(mcp, cancelled.JobId, originalEpoch);
            Assert.AreEqual(cancelled, JobResult(cancelledArchive, originalEpoch));
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-simulation.json"),
                JsonSerializer.Serialize(current), token);

            // Keep the existing native failure-recovery evidence. The MCP deck
            // admission intentionally refuses this file include before dispatch.
            var invalid = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(new()
            {
                Document = simulationDocument, OperationId = Guid.NewGuid().ToString("D"), ProcessEpoch = client.Epoch,
                Netlist = ".include \"/definitely/missing/kicad-automation-model.lib\"\n.op\n.end\n"
            }, token);
            var invalidTerminal = await WaitForTerminal(invalid);
            Assert.AreEqual(SimulationJobStatus.SimjsFailed, invalidTerminal.Status);
            Assert.IsTrue(invalidTerminal.WorkerFinished);
            var invalidRead = JobResult(await mcp.Tool("kicad_simulation_job", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = invalidTerminal.JobId }));
            Assert.AreEqual(SimulationJobStatus.SimjsFailed, invalidRead.Status);

            // The MCP path must retain a terminal native failure and its stable
            // code/message, rather than turning a failed run into a false success.
            string failedOperation = Guid.NewGuid().ToString("D");
            const string failedNetlist = "Automation failure\nR1 in out\n.op\n.end\n";
            var failed = await WaitForTerminal(await Start(failedNetlist, failedOperation));
            Assert.AreEqual(SimulationJobStatus.SimjsFailed, failed.Status, failed.ErrorMessage);
            Assert.AreEqual("simulation_start_failed", failed.ErrorCode);
            Assert.AreEqual("KiCad's native ngspice wrapper rejected the netlist or run command", failed.ErrorMessage);
            Assert.IsTrue(failed.WorkerFinished);
            var failedRead = JobResult(await mcp.Tool("kicad_simulation_job", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = failed.JobId }));
            Assert.AreEqual(failed, failedRead,
                "Reading a failed simulation must preserve the same deterministic terminal receipt.");
            Assert.AreEqual(failed, await Start(failedNetlist, failedOperation),
                "Retrying a failed operation must return its retained failure receipt.");
            var failedArchive = await ArchiveResult(mcp, failed.JobId, originalEpoch);
            Assert.AreEqual(failed, JobResult(failedArchive, originalEpoch));

            const string acNetlist = "Complex RC\nV1 in 0 DC 0 AC 1\nR1 in out 1k\nC1 out 0 1u\n.ac lin 3 100 300\n.end\n";
            string acOperation = Guid.NewGuid().ToString("D");
            var ac = await WaitForTerminal(await Start(acNetlist, acOperation));
            Assert.AreEqual(SimulationJobStatus.SimjsCompleted, ac.Status, ac.ErrorMessage);
            var frequency = ac.Vectors.Single(vector => vector.Name.Equals("frequency", StringComparison.OrdinalIgnoreCase));
            var acInput = ac.Vectors.Single(vector => vector.Name.Equals("in", StringComparison.OrdinalIgnoreCase)
                || vector.Name.Equals("v(in)", StringComparison.OrdinalIgnoreCase));
            var acOutput = ac.Vectors.Single(vector => vector.Name.Equals("out", StringComparison.OrdinalIgnoreCase)
                || vector.Name.Equals("v(out)", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(acInput.Complex, "A native complex vector remains complex even when every imaginary component is zero.");
            Assert.IsTrue(acOutput.Complex, "AC phase information must not be discarded or replaced by magnitude.");
            int pointCount = frequency.Complex ? frequency.Values.Count / 2 : frequency.Values.Count;
            Assert.AreEqual(3, pointCount);
            Assert.AreEqual(2 * pointCount, acInput.Values.Count);
            Assert.AreEqual(2 * pointCount, acOutput.Values.Count);
            for (int point = 0; point < pointCount; point++)
            {
                double hz = frequency.Values[frequency.Complex ? 2 * point : point];
                Assert.AreEqual(100.0 + 100.0 * point, hz, 1e-9);
                if (frequency.Complex) Assert.AreEqual(0.0, frequency.Values[2 * point + 1], 1e-12);
                Assert.AreEqual(1.0, acInput.Values[2 * point], 1e-9);
                Assert.AreEqual(0.0, acInput.Values[2 * point + 1], 1e-12);
                double omegaRc = 2.0 * Math.PI * hz * 1000.0 * 1e-6;
                Assert.AreEqual(1.0 / (1.0 + omegaRc * omegaRc), acOutput.Values[2 * point], 1e-9);
                Assert.AreEqual(-omegaRc / (1.0 + omegaRc * omegaRc), acOutput.Values[2 * point + 1], 1e-9);
            }
            Assert.AreEqual(ac, JobResult(await mcp.Tool("kicad_simulation_job", new
            { instanceId, expectedInstanceEpoch = originalEpoch, document = Target(simulationDocument), jobId = ac.JobId })));
            Assert.AreEqual(ac, await Start(acNetlist, acOperation), "Complex pairs must survive exact operation replay.");
            var complexArchive = await ArchiveResult(mcp, ac.JobId, originalEpoch);
            Assert.AreEqual(ac, JobResult(complexArchive, originalEpoch));
            var laterReal = await WaitForTerminal(await Start(netlist, Guid.NewGuid().ToString("D")));
            Assert.IsTrue(laterReal.Vectors.All(vector => !vector.Complex));
            Assert.IsTrue(JsonElement.DeepEquals(complexArchive.GetProperty("structuredContent"),
                (await ArchiveResult(mcp, ac.JobId, originalEpoch)).GetProperty("structuredContent")),
                "A later real simulation must not change archived complex types or sample pairs.");
            Assert.AreEqual(ac, await Start(acNetlist, acOperation));
            Assert.AreEqual(laterReal, await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = simulationDocument, JobId = laterReal.JobId, ProcessEpoch = originalEpoch }, token),
                "Replaying an archived AC operation must not replace the later native result.");
            await using (var complexReconnect = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                stateDirectory, Path.Combine(evidence, instanceId + "-simulation-complex-reconnect.stderr.log"), token))
            {
                Assert.IsTrue(JsonElement.DeepEquals(complexArchive.GetProperty("structuredContent"),
                    (await ArchiveResult(complexReconnect, ac.JobId, originalEpoch)).GetProperty("structuredContent")),
                    "A fresh MCP process must retain both components and the native complex type.");
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-simulation-complex.json"),
                JsonSerializer.Serialize(complexArchive.GetProperty("structuredContent")), token);

            // The two editor instances run this journey independently. One real
            // restart proves the boundary; the second keeps its fixture process
            // alive so the enclosing two-project cleanup can still verify both
            // registrations.
            if (Interlocked.CompareExchange(ref simulationRestartProofClaimed, 1, 0) != 0)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-simulation-restart.json"),
                    JsonSerializer.Serialize(new { instanceId, result = "restart-proof-covered-by-other-instance" }), token);
                return;
            }

            // The live native job still disappears with the process. Archived
            // receipts retain the old process identity and must also survive a
            // subsequent MCP restart without contacting the replacement editor.
            var beforeRestartSession = await client.HandshakeAsync(token);
            int beforeRestartPid = checked((int)beforeRestartSession.ProcessId);
            using (var oldProcess = Process.GetProcessById(beforeRestartPid))
            {
                oldProcess.Kill();
                await oldProcess.WaitForExitAsync(token);
            }

            JsonElement restarted;
            using (var restartDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                restartDeadline.CancelAfter(TimeSpan.FromSeconds(60));
                while (true)
                {
                    using var list = JsonDocument.Parse(Text(await mcp.Tool("kicad_instances_list", new { })));
                    var view = list.RootElement.EnumerateArray().Single(item =>
                        item.GetProperty("instanceId").GetString() == instanceId);
                    if (view.GetProperty("processState").GetString() == "exited") break;
                    await Task.Delay(100, restartDeadline.Token);
                }
                restarted = await mcp.Tool("kicad_instance_start", new
                {
                    executable = Path.Combine(FindRoot(), "automation", "artifacts", "native", "kicad", "kicad"),
                    projectPath = Path.Combine(document.Project.Path, document.Project.Name + ".kicad_pro")
                });
            }
            Assert.IsFalse(restarted.TryGetProperty("isError", out var restartError) && restartError.GetBoolean(), restarted.GetRawText());
            var restartedData = restarted.GetProperty("structuredContent");
            string restartedEpoch = restartedData.GetProperty("epoch").GetString()!;
            Assert.AreNotEqual(beforeRestartSession.Epoch, restartedEpoch, "A restarted native process must receive a new epoch.");
            Assert.AreEqual("running", restartedData.GetProperty("processState").GetString(), restarted.GetRawText());

            var lost = await mcp.Tool("kicad_simulation_job", new
            {
                instanceId, expectedInstanceEpoch = restartedEpoch, document = Target(simulationDocument), jobId = started.JobId
            });
            AssertFailure(lost, "simulation_result_lost");
            await using (var archiveHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                stateDirectory, Path.Combine(evidence, instanceId + "-simulation-archive-restart.stderr.log"), token))
            {
                foreach (var prior in new[] { completedArchive, cancelledArchive, failedArchive, complexArchive })
                {
                    string archivedJobId = prior.GetProperty("structuredContent").GetProperty("job").GetProperty("jobId").GetString()!;
                    var retained = await ArchiveResult(archiveHost, archivedJobId, originalEpoch);
                    Assert.IsTrue(JsonElement.DeepEquals(prior.GetProperty("structuredContent"), retained.GetProperty("structuredContent")),
                        "Both native and MCP restart must preserve exact terminal state, vectors, and provenance.");
                }
                AssertFailure(await archiveHost.Tool("kicad_simulation_result", new
                { instanceId, processEpoch = restartedEpoch, document = Target(simulationDocument), jobId = current.JobId }),
                    "simulation_result_not_found");
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-simulation-restart.json"),
                JsonSerializer.Serialize(new
                {
                    instanceId, oldEpoch = beforeRestartSession.Epoch, oldProcessId = beforeRestartPid,
                    restartedEpoch, restartedProcessId = restartedData.GetProperty("processId").GetInt32(),
                    jobId = started.JobId, liveResult = "simulation_result_lost", nativeRetention = false,
                    archivedCompleted = completedArchive.GetProperty("structuredContent"),
                    archivedCancelled = cancelledArchive.GetProperty("structuredContent"),
                    archivedFailed = failedArchive.GetProperty("structuredContent"),
                    archivedComplex = complexArchive.GetProperty("structuredContent"), archiveRetainedOriginalIdentity = true
                }), token);

            int restartedPid = restartedData.GetProperty("processId").GetInt32();
            string restartedEndpoint = NativeIpcEndpoint.FromSocketPath(
                Path.Combine(NativeIpcEndpoint.RuntimeDirectory(instanceId), "api.sock"));
            await registry.AttachAsync(restartedEndpoint, instanceId, token);
            retainProcess(Process.GetProcessById(restartedPid));

            JsonObject Target(DocumentSpecifier value, bool numericType = false)
            {
                var target = JsonNode.Parse(SchematicJson.Formatter.Format(value))!.AsObject();
                if (numericType) target["type"] = (int)value.Type;
                return target;
            }

            async Task<SimulationJobState> Start(string deck, string operationId, bool numericType = false)
                => JobResult(await mcp.Tool("kicad_simulation_start", new
                { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument, numericType), netlist = deck, operationId }));

            async Task<SimulationJobState> WaitForTerminal(SimulationJobState job)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var currentJob = job;
                while (currentJob.Status == SimulationJobStatus.SimjsRunning)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    currentJob = JobResult(await mcp.Tool("kicad_simulation_wait", new
                    {
                        instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument),
                        jobId = job.JobId, afterSequence = currentJob.Sequence
                    }).WaitAsync(deadline.Token));
                }
                return currentJob;
            }

            async Task<JsonElement> ArchiveResult(StdioMcpFixture host, string jobId, string processEpoch)
            {
                var reply = await host.Tool("kicad_simulation_result", new
                { instanceId, processEpoch, document = Target(simulationDocument), jobId });
                Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
                Assert.AreEqual("archive", reply.GetProperty("structuredContent").GetProperty("source").GetString());
                return reply;
            }

            SimulationJobState JobResult(JsonElement reply, string? expectedEpoch = null)
            {
                expectedEpoch ??= client.Epoch;
                Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
                var data = reply.GetProperty("structuredContent");
                Assert.AreEqual(instanceId, data.GetProperty("instanceId").GetString());
                Assert.AreEqual(expectedEpoch, data.GetProperty("instanceEpoch").GetString());
                Assert.IsTrue(JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(simulationDocument, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    data.GetProperty("document")), "MCP must preserve the complete project and every native sheet UUID segment.");
                var job = JsonNode.Parse(data.GetProperty("job").GetRawText())!.AsObject();
                job["status"] = (int)Enum.Parse<SimulationJobStatus>(job["status"]!.GetValue<string>());
                job["document"] = Target(simulationDocument);
                job["processEpoch"] = expectedEpoch;
                return SchematicJson.Parser.Parse<SimulationJobState>(job.ToJsonString());
            }

            void AssertFailure(JsonElement reply, string code)
            {
                Assert.IsTrue(reply.GetProperty("isError").GetBoolean(), reply.GetRawText());
                Assert.AreEqual(code, reply.GetProperty("structuredContent").GetProperty("errorCode").GetString(), reply.GetRawText());
            }
        }
        finally { Directory.Delete(stateDirectory, true); }
    }

    private static async Task VerifyGeneratedSimulationDependenciesThroughMcp(NativeClient native,
        StdioMcpFixture mcp, DocumentSpecifier document, SimulationJobState job,
        string evidence, string instanceId, CancellationToken token)
    {
        var nativeDependencies = await native.InvokeAsync<NativeCapabilitySimulationReadDependencies,
            NativeCapabilitySimulationDependencies>(new()
            { Document = document, JobId = job.JobId, ProcessEpoch = native.Epoch }, token);
        Assert.AreEqual(document, nativeDependencies.Document);
        Assert.AreEqual(job.JobId, nativeDependencies.JobId);
        Assert.AreEqual(job.OperationId, nativeDependencies.OperationId);
        Assert.AreEqual(native.Epoch, nativeDependencies.ProcessEpoch);

        var reply = await mcp.Tool("kicad_simulation_dependencies", new
        {
            instanceId,
            expectedInstanceEpoch = native.Epoch,
            documentJson = SchematicJson.Formatter.Format(document),
            jobId = job.JobId
        });
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-simulation-dependencies.json"), reply.GetRawText(), token);
        Assert.IsFalse(reply.TryGetProperty("isError", out var dependencyError) && dependencyError.GetBoolean(), reply.GetRawText());
        var data = reply.GetProperty("structuredContent");
        Assert.AreEqual(instanceId, data.GetProperty("instanceId").GetString());
        Assert.AreEqual(native.Epoch, data.GetProperty("instanceEpoch").GetString());
        Assert.IsTrue(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            data.GetProperty("document")),
            "The MCP dependency result must preserve the exact schematic document identity.");
        Assert.AreEqual(job.JobId, data.GetProperty("jobId").GetString());
        Assert.AreEqual(job.OperationId, data.GetProperty("operationId").GetString());
        string generatedHash = data.GetProperty("generatedDeckSha256").GetString()!;
        Assert.AreEqual(64, generatedHash.Length, "The generated simulation deck must have a SHA-256 fingerprint.");
        Assert.IsTrue(generatedHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.AreEqual(nativeDependencies.GeneratedDeckSha256, generatedHash,
            "The MCP must expose the native retained generated deck hash.");
        Assert.AreNotEqual(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), generatedHash,
            "An empty caller netlist must fingerprint the actual generated deck, not the empty request.");
        Assert.IsFalse(data.GetProperty("dependencyCaptureComplete").GetBoolean(),
            "Unenumerated external dependencies must remain explicitly incomplete.");
        var warnings = data.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray();
        Assert.IsTrue(warnings.Any(warning => !string.IsNullOrWhiteSpace(warning)),
            "Incomplete dependency capture must include an explicit warning.");
        CollectionAssert.AreEqual(nativeDependencies.Warnings.ToArray(), warnings);
        CollectionAssert.AreEqual(nativeDependencies.DependencyPaths.ToArray(),
            data.GetProperty("dependencyPaths").EnumerateArray().Select(path => path.GetString()).ToArray());
    }
}
