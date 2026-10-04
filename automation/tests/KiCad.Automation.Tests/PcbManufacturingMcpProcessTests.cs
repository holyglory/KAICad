using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>
/// Managed black-box checks for the typed manufacturing boundary.  These launch
/// the compiled MCP server over STDIO but never contact KiCad or a native editor.
/// </summary>
[TestClass]
public sealed class PcbManufacturingMcpProcessTests
{
    [TestMethod]
    public async Task TypedManufacturingToolPublishesJobSchemaAndRejectsMalformedIdentity()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-manufacturing-mcp-schema-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tools = listed.GetProperty("tools");
            JsonElement export = tools.EnumerateArray().Single(tool =>
                tool.GetProperty("name").GetString() == "kicad_pcb_export");

            JsonElement input = export.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
            AssertObject(input, "typed manufacturing input");
            JsonElement inputProperties = input.GetProperty("properties");
            foreach (string property in new[] { "operationId", "document", "expectedState", "outputDirectory", "job" })
                Assert.IsTrue(inputProperties.TryGetProperty(property, out _),
                    $"The typed manufacturing schema is missing {property}.");

            JsonElement job = inputProperties.GetProperty("job");
            AssertObject(job, "manufacturing job envelope");
            JsonElement jobProperties = job.GetProperty("properties");
            foreach (string property in new[]
            {
                "export3D", "exportRender", "exportSvg", "exportDxf", "exportPdf", "exportPs",
                "exportGerbers", "exportDrill", "exportPosition", "exportGencad", "exportIpc2581",
                "exportIpcD356", "exportOdb", "exportStats"
            })
                Assert.IsTrue(jobProperties.TryGetProperty(property, out _),
                    $"The typed manufacturing job schema is missing {property}.");

            JsonElement malformed = await mcp.Tool("kicad_pcb_export", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new
                {
                    operationId = "not-a-uuid",
                    document = new { },
                    expectedState = new { },
                    outputDirectory = Path.Combine(root, "out"),
                    job = new { exportSvg = new { } }
                }
            });
            Assert.IsTrue(malformed.GetProperty("isError").GetBoolean(), malformed.GetRawText());
            Assert.AreEqual("invalid_operation_id",
                malformed.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
                malformed.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertObject(JsonElement element, string label)
    {
        Assert.IsTrue(element.TryGetProperty("type", out JsonElement type)
            && ((type.ValueKind == JsonValueKind.String && type.GetString() == "object")
                || (type.ValueKind == JsonValueKind.Array
                    && type.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                        && value.GetString() == "object"))),
            $"{label} must expose an object schema: {element.GetRawText()}");
    }
}
