using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ProjectSettingsMcpProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StructuredProjectSettingsAreDiscoveredAndValidatedOverStdio()
    {
        string root = Directory.CreateTempSubdirectory("kicad-project-settings-mcp-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(UpdateCommandTests.StartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tools = listed.GetProperty("tools");
            string[] names = ["kicad_project_net_classes", "kicad_project_net_classes_set",
                "kicad_project_text_variables", "kicad_project_text_variables_set"];
            JsonElement[] schemas = names.Select(name => tools.EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == name)).ToArray();
            foreach (JsonElement tool in schemas)
            {
                JsonElement schema = tool.GetProperty("inputSchema");
                Assert.IsTrue(schema.GetProperty("required").EnumerateArray().Any(value => value.GetString() == "instanceId"));
                Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("document", out _), tool.GetRawText());
            }
            foreach (JsonElement tool in new[] { schemas[0], schemas[2], schemas[3] })
            {
                JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");
                Assert.IsTrue(properties.TryGetProperty("expectedStateJson", out _), tool.GetRawText());
                JsonElement state = properties.GetProperty("expectedState").GetProperty("properties");
                foreach (string field in new[] { "document", "revision", "processEpoch", "nativeFiles", "fileBaselines" })
                    Assert.IsTrue(state.TryGetProperty(field, out _), tool.GetRawText());
            }
            JsonElement write = schemas[1].GetProperty("inputSchema");
            string[] required = write.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "operationId", "expectedStateJson", "expectedNetClassesSha256", "mergeMode" }, required);
            JsonElement classes = write.GetProperty("properties").GetProperty("netClasses").GetProperty("properties")
                .GetProperty("netClasses").GetProperty("items").GetProperty("properties");
            foreach (string field in new[] { "name", "priority", "type", "constituents", "board", "schematic" })
                Assert.IsTrue(classes.TryGetProperty(field, out _), write.GetRawText());
            JsonElement board = classes.GetProperty("board").GetProperty("properties");
            foreach (string field in new[] { "clearanceNm", "trackWidthNm", "diffPairTrackWidthNm", "diffPairGapNm",
                         "diffPairViaGapNm", "viaStack", "microviaStack", "color", "tuningProfile" })
                Assert.IsTrue(board.TryGetProperty(field, out _), write.GetRawText());
            Assert.IsTrue(board.GetProperty("viaStack").GetProperty("properties").TryGetProperty("drillDiameterNm", out _));
            JsonElement schematic = classes.GetProperty("schematic").GetProperty("properties");
            foreach (string field in new[] { "wireWidthNm", "busWidthNm", "color", "lineStyle" })
                Assert.IsTrue(schematic.TryGetProperty(field, out _), write.GetRawText());

            string instanceId = Guid.NewGuid().ToString("D");
            var project = new { name = "typed-project", path = root };
            var document = new { type = "DOCTYPE_PCB", project, boardFilename = "typed-project.kicad_pcb" };
            var operationId = Guid.NewGuid().ToString("D");
            JsonElement missing = await mcp.Tool("kicad_project_net_classes", new { instanceId, document });
            AssertError(missing, "invalid_project_settings_input");
            JsonElement conflict = await mcp.Tool("kicad_project_net_classes", new
            { instanceId, documentJson = "", document, expectedStateJson = "{}" });
            AssertError(conflict, "invalid_project_settings_input");

            JsonElement mutationConflict = await mcp.Tool("kicad_project_net_classes_set", new
            {
                instanceId, document, expectedStateJson = "{}", mergeMode = "MMM_REPLACE", operationId,
                expectedNetClassesSha256 = new string('a', 64), netClassesJson = "{}",
                netClasses = new { netClasses = Array.Empty<object>() }
            });
            AssertError(mutationConflict, "invalid_project_settings_input");

            JsonElement duplicate = await mcp.Tool("kicad_project_net_classes_set", new
            {
                instanceId, document, expectedStateJson = "{}", mergeMode = "MMM_REPLACE", operationId,
                expectedNetClassesSha256 = new string('a', 64),
                netClasses = new { netClasses = new[] { new { name = "Duplicate" }, new { name = "Duplicate" } } }
            });
            AssertError(duplicate, "invalid_project_net_classes_request");
            JsonElement nested = await mcp.Tool("kicad_project_net_classes_set", new
            {
                instanceId, document, expectedStateJson = "{}", mergeMode = "MMM_MERGE", operationId,
                expectedNetClassesSha256 = new string('a', 64),
                netClasses = new { netClasses = new object[] { new { name = "Default" }, new
                { name = "InvalidSecond", board = new { viaStack = new { diameterNm = 100, drillDiameterNm = 101 } } } } }
            });
            AssertError(nested, "invalid_project_net_classes_request");
            JsonElement supported = await mcp.Tool("kicad_project_net_classes_set", new
            {
                instanceId, document, expectedStateJson = "{}", mergeMode = "MMM_MERGE", operationId,
                expectedNetClassesSha256 = new string('a', 64),
                netClasses = new { netClasses = new object[] { new { name = "Default" }, new
                { name = "Signal", board = new { trackWidthNm = 200000, diffPairGapNm = 150000 },
                    schematic = new { lineStyle = "SLS_DASH" } } } }
            });
            AssertError(supported, "project_net_classes_schematic_required");

            JsonElement variableConflict = await mcp.Tool("kicad_project_text_variables_set", new
            {
                instanceId, project, document, variables = new { OWNER = "typed" }, mergeMode = "MMM_REPLACE",
                operationId, expectedStateJson = "", expectedState = new { }
            });
            AssertError(variableConflict, "invalid_project_settings_input");

            string file = Path.Combine(root, "typed-project.kicad_pcb");
            var state = new
            {
                document, processEpoch = Guid.NewGuid().ToString("D"),
                revision = new { epoch = Guid.NewGuid().ToString("D"), sequence = 7 },
                nativeIdentity = Guid.NewGuid().ToString("D"), stateSha256 = new string('a', 64),
                scope = "DLS_PCB", projectSettingsIncluded = true,
                nativeFiles = new[] { file }, fileBaselines = new[] { new { path = file, baselinePath = file } }
            };
            JsonElement bound = await mcp.Tool("kicad_project_net_classes", new { instanceId, document, expectedState = state });
            AssertError(bound, "unknown_instance");
            JsonElement variableRead = await mcp.Tool("kicad_project_text_variables", new { instanceId, project, document, expectedState = state });
            AssertError(variableRead, "unknown_instance");
            JsonElement variableWrite = await mcp.Tool("kicad_project_text_variables_set", new
            {
                instanceId, project, document, variables = new { OWNER = "typed", EMPTY = "" },
                mergeMode = "MMM_MERGE", operationId, expectedState = state
            });
            AssertError(variableWrite, "unknown_instance");

            string evidence = Path.Combine(TestContext.TestResultsDirectory!, "project-settings-mcp-schema-and-admission.json");
            await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new
            {
                schemas,
                observed = new { missing, conflict, mutationConflict, duplicate, nested, supported,
                    variableConflict, bound, variableRead, variableWrite }
            }), timeout.Token);
            TestContext.AddResultFile(evidence);
        }
        finally
        {
            string log = Path.Combine(root, "stderr.log");
            if (File.Exists(log))
            {
                string retained = Path.Combine(TestContext.TestResultsDirectory!, "project-settings-mcp.log");
                File.Copy(log, retained, true);
                TestContext.AddResultFile(retained);
            }
            Directory.Delete(root, true);
        }
    }

    private static void AssertError(JsonElement response, string code)
    {
        Assert.IsTrue(response.GetProperty("isError").GetBoolean(), response.GetRawText());
        Assert.AreEqual(code, response.GetProperty("structuredContent").GetProperty("errorCode").GetString(), response.GetRawText());
    }
}
