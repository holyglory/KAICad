using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeSimulation(NativeClient client, DocumentSpecifier document,
        string evidence, string instanceId, CancellationToken token)
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
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
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

            var completedRead = JobResult(await mcp.Tool("kicad_simulation_job", new
            { instanceId, expectedInstanceEpoch = client.Epoch, document = Target(simulationDocument), jobId = started.JobId }));
            Assert.AreEqual(SimulationJobStatus.SimjsCompleted, completedRead.Status);
            Assert.AreEqual(current, completedRead, "The MCP job read must retain the complete native result.");

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
            foreach (string name in new[] { "kicad_simulation_job", "kicad_simulation_wait", "kicad_simulation_cancel" })
            {
                var arguments = new Dictionary<string, object>
                {
                    ["instanceId"] = instanceId, ["expectedInstanceEpoch"] = client.Epoch,
                    ["document"] = Target(document), ["jobId"] = started.JobId
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

            SimulationJobState JobResult(JsonElement reply)
            {
                Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.GetRawText());
                var data = reply.GetProperty("structuredContent");
                Assert.AreEqual(instanceId, data.GetProperty("instanceId").GetString());
                Assert.AreEqual(client.Epoch, data.GetProperty("instanceEpoch").GetString());
                Assert.IsTrue(JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(simulationDocument, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    data.GetProperty("document")), "MCP must preserve the complete project and every native sheet UUID segment.");
                var job = JsonNode.Parse(data.GetProperty("job").GetRawText())!.AsObject();
                job["status"] = (int)Enum.Parse<SimulationJobStatus>(job["status"]!.GetValue<string>());
                job["document"] = Target(simulationDocument);
                job["processEpoch"] = client.Epoch;
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
