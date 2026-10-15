using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Extends the compiled STDIO acceptance surface for the new structured boundary.
// Seeded receipts exercise real managed persistence/replay, not native simulation execution.
[TestClass]
public sealed class SimulationTypedMcpProcessTests
{
    private static readonly string[] TypedTools =
    [
        "kicad_simulation_start_typed", "kicad_simulation_job_typed", "kicad_simulation_operation_typed",
        "kicad_simulation_wait_typed", "kicad_simulation_cancel_typed", "kicad_simulation_result_typed",
        "kicad_simulation_vector_typed", "kicad_simulation_dependencies_typed"
    ];

    [TestMethod]
    public async Task TypedLifecyclePublishesSchemasAndRefusesInvalidBindings()
    {
        string root = Directory.CreateTempSubdirectory("kicad-simulation-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement catalog = await mcp.ListTools(null);
            JsonElement[] tools = catalog.GetProperty("tools").EnumerateArray().ToArray();
            foreach (string name in TypedTools)
            {
                JsonElement tool = tools.Single(value => value.GetProperty("name").GetString() == name);
                Assert.IsTrue(tools.Any(value => value.GetProperty("name").GetString() == name[..^6]),
                    "The existing simulation entry point must remain discoverable: " + name);
                JsonElement schema = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
                AssertType(schema, "object");
                JsonElement properties = schema.GetProperty("properties");
                AssertType(properties.GetProperty("processEpoch"), "string");
                AssertType(properties.GetProperty(name is "kicad_simulation_start_typed" or "kicad_simulation_operation_typed"
                    ? "operationId" : "jobId"), "string");
                JsonElement document = properties.GetProperty("document");
                AssertType(document, "object");
                Assert.IsFalse(properties.TryGetProperty("documentJson", out _), name);
                JsonElement documentProperties = document.GetProperty("properties");
                CollectionAssert.Contains(documentProperties.GetProperty("type").GetProperty("enum").EnumerateArray()
                    .Select(value => value.GetString()).ToArray(), "DOCTYPE_SCHEMATIC");
                AssertType(documentProperties.GetProperty("project"), "object");
                AssertType(documentProperties.GetProperty("sheetPath").GetProperty("properties").GetProperty("path"), "array");
            }

            string instanceId = Guid.NewGuid().ToString("D");
            var empty = new { instanceId, input = new { } };
            AssertError(await mcp.Tool("kicad_simulation_start_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_job_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_operation_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_wait_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_cancel_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_result_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_vector_typed", empty), "invalid_document_target");
            AssertError(await mcp.Tool("kicad_simulation_dependencies_typed", empty), "invalid_document_target");

            JsonObject documentInput = Document(root);
            string epoch = Guid.NewGuid().ToString("D");
            var operationInput = Input(documentInput, epoch, "operationId", Guid.NewGuid().ToString("D"));
            var jobInput = Input(documentInput, epoch, "jobId", Guid.NewGuid().ToString("D"));
            var vectorInput = jobInput.DeepClone().AsObject(); vectorInput["vectorName"] = "v(out)";
            AssertError(await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = operationInput }), "unknown_instance");
            AssertError(await mcp.Tool("kicad_simulation_operation_typed", new { instanceId, input = operationInput }), "unknown_instance");
            AssertError(await mcp.Tool("kicad_simulation_job_typed", new { instanceId, input = jobInput }), "unknown_instance");
            AssertError(await mcp.Tool("kicad_simulation_wait_typed", new { instanceId, input = jobInput }), "unknown_instance");
            AssertError(await mcp.Tool("kicad_simulation_cancel_typed", new { instanceId, input = jobInput }), "unknown_instance");
            AssertError(await mcp.Tool("kicad_simulation_dependencies_typed", new { instanceId, input = jobInput }), "unknown_instance");
            AssertError(await mcp.Tool("kicad_simulation_result_typed", new { instanceId, input = jobInput }), "simulation_result_not_found");
            AssertError(await mcp.Tool("kicad_simulation_vector_typed", new { instanceId, input = vectorInput }), "simulation_result_not_found");

            foreach (string badId in new[] { "", Guid.Empty.ToString("D"), "bad-id", "A2222222-2222-4222-8222-222222222222" })
            {
                JsonObject badEpoch = operationInput.DeepClone().AsObject(); badEpoch["processEpoch"] = badId;
                AssertError(await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = badEpoch }), "invalid_simulation_request");
                JsonObject badOperation = operationInput.DeepClone().AsObject(); badOperation["operationId"] = badId;
                AssertError(await mcp.Tool("kicad_simulation_operation_typed", new { instanceId, input = badOperation }), "invalid_simulation_request");
                JsonObject badJob = jobInput.DeepClone().AsObject(); badJob["jobId"] = badId;
                AssertError(await mcp.Tool("kicad_simulation_cancel_typed", new { instanceId, input = badJob }), "invalid_simulation_request");
            }

            JsonObject board = JsonSerializer.SerializeToNode(new
            {
                type = "DOCTYPE_PCB", project = new { name = "simulation-typed", path = root }, boardFilename = "board.kicad_pcb"
            })!.AsObject();
            AssertError(await mcp.Tool("kicad_simulation_start_typed", new
            { instanceId, input = Input(board, epoch, "operationId", Guid.NewGuid().ToString("D")) }), "unsupported_simulation_document");

            JsonObject ambiguous = operationInput.DeepClone().AsObject(); ambiguous["document"]!["boardFilename"] = "board.kicad_pcb";
            AssertError(await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = ambiguous }), "invalid_document_target");
            JsonObject forbiddenDeck = operationInput.DeepClone().AsObject(); forbiddenDeck["netlist"] = "Unsafe\n.control\nshell echo bad\n.endc\n.end\n";
            AssertError(await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = forbiddenDeck }), "simulation_deck_rejected");
            JsonObject invalidVector = vectorInput.DeepClone().AsObject(); invalidVector["sampleStart"] = -1;
            AssertError(await mcp.Tool("kicad_simulation_vector_typed", new { instanceId, input = invalidVector }), "invalid_simulation_request");

            string duplicateEpoch = operationInput.ToJsonString().Replace("\"processEpoch\":", "\"processEpoch\":\"" + epoch + "\",\"processEpoch\":", StringComparison.Ordinal);
            using var repeated = JsonDocument.Parse(duplicateEpoch);
            Assert.IsTrue((await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = repeated.RootElement }))
                .GetProperty("isError").GetBoolean(), "Repeated input identities must fail at deserialization.");
            JsonObject unknown = jobInput.DeepClone().AsObject(); unknown["futureSetting"] = true;
            Assert.IsTrue((await mcp.Tool("kicad_simulation_job_typed", new { instanceId, input = unknown }))
                .GetProperty("isError").GetBoolean(), "Unknown input members must not be silently ignored.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TypedArchiveReplayPreservesOriginalIdentityAndComplexSamples()
    {
        string root = Directory.CreateTempSubdirectory("kicad-simulation-archive-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            string state = Path.Combine(root, "state");
            string instanceId = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string jobId = Guid.NewGuid().ToString("D");
            Guid operationId = Guid.NewGuid();
            JsonObject documentInput = Document(root);
            DocumentSpecifier document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentInput.ToJsonString());
            const string deck = "Divider\nV1 in 0 DC 5\nR1 in out 1k\nR2 out 0 1k\n.op\n.end\n";
            string deckHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deck)));
            var operation = SimulationOperationReceipt.Prepare(instanceId, epoch, document, operationId, deckHash, "inline-caller-deck")
                with { Stage = SimulationOperationStage.Accepted, JobId = jobId };
            var job = new SimulationJobState
            {
                Document = document.Clone(), ProcessEpoch = epoch, JobId = jobId, OperationId = operationId.ToString("D"),
                Status = SimulationJobStatus.SimjsCompleted, Sequence = 9, Progress = 100, WorkerFinished = true
            };
            job.Vectors.Add(new SimulationVector { Name = "v(out)", Complex = true, Values = { 1.0, 0.0, 0.5, -0.25 } });
            job.Vectors.Add(new SimulationVector { Name = "frequency", Values = { 100.0, 200.0 } });
            var dependencies = new NativeCapabilitySimulationDependencies
            {
                Document = document.Clone(), ProcessEpoch = epoch, JobId = jobId, OperationId = operationId.ToString("D"),
                GeneratedDeckSha256 = deckHash, DependencyCaptureComplete = false
            };
            dependencies.Warnings.Add("External dependency enumeration is incomplete.");
            var store = new SimulationResultReceipts(state);
            store.WriteOperation(operation with { Stage = SimulationOperationStage.Prepared, JobId = null });
            store.WriteOperation(operation with { Stage = SimulationOperationStage.Dispatched, JobId = null });
            store.WriteOperation(operation); store.MakeCurrent(operation);
            SimulationTerminalReceipt terminal = SimulationTerminalReceipt.Capture(operation, job, dependencies);
            store.WriteTerminal(terminal);

            JsonObject startInput = Input(documentInput, epoch, "operationId", operationId.ToString("D")); startInput["netlist"] = deck;
            JsonObject jobInput = Input(documentInput, epoch, "jobId", jobId);
            JsonObject vectorInput = jobInput.DeepClone().AsObject();
            vectorInput["vectorName"] = "v(out)"; vectorInput["sampleStart"] = 1; vectorInput["sampleCount"] = 1;
            for (int restart = 0; restart < 2; restart++)
            {
                await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                    state, Path.Combine(root, "stderr-" + restart + ".log"), timeout.Token);
                JsonElement replay = Data(await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = startInput }));
                JsonElement archived = Data(await mcp.Tool("kicad_simulation_result_typed", new { instanceId, input = jobInput }));
                Assert.AreEqual(replay.GetRawText(), archived.GetRawText(), "Identical starts must replay the original archive without a native instance.");
                JsonElement cancelled = Data(await mcp.Tool("kicad_simulation_cancel_typed", new { instanceId, input = jobInput }));
                Assert.AreEqual(archived.GetRawText(), cancelled.GetRawText(),
                    "A late cancellation retry must replay the exact terminal archive without contacting a native instance.");
                Assert.AreEqual("archive", archived.GetProperty("source").GetString());
                Assert.AreEqual(terminal.Sha256, archived.GetProperty("receiptSha256").GetString());
                Assert.AreEqual(epoch, archived.GetProperty("instanceEpoch").GetString());
                Assert.AreEqual(operationId.ToString("D"), archived.GetProperty("job").GetProperty("operationId").GetString());
                Assert.AreEqual(2, archived.GetProperty("document").GetProperty("sheetPath").GetProperty("path").GetArrayLength());
                Assert.IsFalse(archived.GetProperty("dependencies").GetProperty("dependencyCaptureComplete").GetBoolean());
                Assert.AreEqual(dependencies.Warnings[0], archived.GetProperty("dependencies").GetProperty("warnings")[0].GetString());

                JsonElement archivedDependencies = Data(await mcp.Tool("kicad_simulation_dependencies_typed",
                    new { instanceId, input = jobInput }));
                Assert.AreEqual("archive", archivedDependencies.GetProperty("source").GetString());
                Assert.AreEqual(terminal.Sha256, archivedDependencies.GetProperty("receiptSha256").GetString());
                Assert.AreEqual(epoch, archivedDependencies.GetProperty("instanceEpoch").GetString());
                Assert.AreEqual(jobId, archivedDependencies.GetProperty("jobId").GetString());
                Assert.AreEqual(operationId.ToString("D"), archivedDependencies.GetProperty("operationId").GetString());
                Assert.AreEqual(deckHash, archivedDependencies.GetProperty("generatedDeckSha256").GetString());
                Assert.IsFalse(archivedDependencies.GetProperty("dependencyCaptureComplete").GetBoolean());
                CollectionAssert.AreEqual(dependencies.Warnings.ToArray(), archivedDependencies.GetProperty("warnings")
                    .EnumerateArray().Select(value => value.GetString()).ToArray());

                JsonElement vector = Data(await mcp.Tool("kicad_simulation_vector_typed", new { instanceId, input = vectorInput }))
                    .GetProperty("vector");
                Assert.IsTrue(vector.GetProperty("complex").GetBoolean());
                Assert.AreEqual("real-imaginary-pairs", vector.GetProperty("encoding").GetString());
                CollectionAssert.AreEqual(new[] { 0.5, -0.25 }, Values(vector.GetProperty("values")));
                CollectionAssert.AreEqual(new[] { 0.5 }, Values(vector.GetProperty("realValues")));
                CollectionAssert.AreEqual(new[] { -0.25 }, Values(vector.GetProperty("imaginaryValues")));
                JsonObject zeroImaginary = jobInput.DeepClone().AsObject(); zeroImaginary["vectorName"] = "v(out)"; zeroImaginary["sampleCount"] = 1;
                JsonElement zero = Data(await mcp.Tool("kicad_simulation_vector_typed", new { instanceId, input = zeroImaginary })).GetProperty("vector");
                Assert.IsTrue(zero.GetProperty("complex").GetBoolean(), "Native complex type is preserved when the imaginary value is zero.");
                CollectionAssert.AreEqual(new[] { 1.0, 0.0 }, Values(zero.GetProperty("values")));

                JsonElement legacy = Data(await mcp.Tool("kicad_simulation_result", new
                { instanceId, processEpoch = epoch, document = documentInput, jobId }));
                Assert.AreEqual(archived.GetRawText(), legacy.GetRawText(), "Legacy and shared document schemas must preserve the exact same original identity.");
                JsonObject replacementEpoch = jobInput.DeepClone().AsObject(); replacementEpoch["processEpoch"] = Guid.NewGuid().ToString("D");
                AssertError(await mcp.Tool("kicad_simulation_result_typed", new { instanceId, input = replacementEpoch }), "simulation_result_not_found");
                JsonObject wrongSheet = jobInput.DeepClone().AsObject(); wrongSheet["document"]!["sheetPath"]!["path"]![1]!["value"] = Guid.NewGuid().ToString("D");
                AssertError(await mcp.Tool("kicad_simulation_result_typed", new { instanceId, input = wrongSheet }), "simulation_result_not_found");
                JsonObject changedDeck = startInput.DeepClone().AsObject(); changedDeck["netlist"] = deck.Replace("DC 5", "DC 6", StringComparison.Ordinal);
                AssertError(await mcp.Tool("kicad_simulation_start_typed", new { instanceId, input = changedDeck }), "simulation_operation_reused");
            }
            Assert.AreEqual(terminal, new SimulationResultReceipts(state).ReadTerminal(instanceId, epoch, Guid.Parse(jobId)),
                "Reading and replaying must not replace the saved native result.");

            string unresolvedInstance = Guid.NewGuid().ToString("D");
            Guid unresolvedId = Guid.NewGuid();
            var dispatched = SimulationOperationReceipt.Prepare(unresolvedInstance, epoch, document, unresolvedId, deckHash, "inline-caller-deck")
                with { Stage = SimulationOperationStage.Dispatched };
            store.WriteOperation(dispatched with { Stage = SimulationOperationStage.Prepared });
            store.WriteOperation(dispatched); store.MakeCurrent(dispatched);
            JsonObject unresolved = Input(documentInput, epoch, "operationId", unresolvedId.ToString("D")); unresolved["netlist"] = deck;
            await using var finalMcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                state, Path.Combine(root, "stderr-unresolved.log"), timeout.Token);
            AssertError(await finalMcp.Tool("kicad_simulation_start_typed", new { instanceId = unresolvedInstance, input = unresolved }),
                "simulation_operation_unresolved");
            Assert.AreEqual(dispatched, store.ReadOperation(unresolvedInstance, unresolvedId), "A lost reply must not authorize a replacement simulation.");

            // An accepted native job can lose its terminal archive if the MCP
            // host disappears after KiCad accepted the request.  Preserve the
            // same no-duplicate-dispatch guarantee when the native instance is
            // no longer attached; an accepted receipt is already evidence that
            // the operation may have executed.
            string acceptedInstance = Guid.NewGuid().ToString("D");
            Guid acceptedId = Guid.NewGuid();
            string acceptedJobId = Guid.NewGuid().ToString("D");
            var accepted = SimulationOperationReceipt.Prepare(acceptedInstance, epoch, document, acceptedId, deckHash, "inline-caller-deck")
                with { Stage = SimulationOperationStage.Accepted, JobId = acceptedJobId };
            store.WriteOperation(accepted with { Stage = SimulationOperationStage.Prepared, JobId = null });
            store.WriteOperation(accepted with { Stage = SimulationOperationStage.Dispatched, JobId = null });
            store.WriteOperation(accepted); store.MakeCurrent(accepted);
            JsonObject acceptedInput = Input(documentInput, epoch, "operationId", acceptedId.ToString("D")); acceptedInput["netlist"] = deck;
            AssertError(await finalMcp.Tool("kicad_simulation_start_typed", new { instanceId = acceptedInstance, input = acceptedInput }),
                "simulation_operation_unresolved");
            Assert.AreEqual(accepted, store.ReadOperation(acceptedInstance, acceptedId),
                "An accepted job without a terminal archive must remain unresolved after instance loss.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static JsonObject Document(string projectPath) => JsonSerializer.SerializeToNode(new
    {
        type = "DOCTYPE_SCHEMATIC", project = new { name = "simulation-typed", path = projectPath },
        sheetPath = new
        {
            path = new[] { new { value = Guid.NewGuid().ToString("D") }, new { value = Guid.NewGuid().ToString("D") } },
            pathHumanReadable = "/child"
        }
    })!.AsObject();

    private static JsonObject Input(JsonObject document, string processEpoch, string identityName, string identity) =>
        new() { ["document"] = document.DeepClone(), ["processEpoch"] = processEpoch, [identityName] = identity };

    private static double[] Values(JsonElement values) => values.EnumerateArray().Select(value => value.GetDouble()).ToArray();

    private static void AssertType(JsonElement schema, string expected)
    {
        JsonElement type = schema.GetProperty("type");
        Assert.IsTrue(type.ValueKind == JsonValueKind.String ? type.GetString() == expected
            : type.EnumerateArray().Any(value => value.GetString() == expected), schema.GetRawText());
    }

    private static JsonElement Data(JsonElement result)
    {
        Assert.IsFalse(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static void AssertError(JsonElement result, string code)
    {
        Assert.IsTrue(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        Assert.AreEqual(code, result.GetProperty("structuredContent").GetProperty("errorCode").GetString(), result.GetRawText());
    }
}
