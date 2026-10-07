using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>Compiled MCP STDIO coverage for the typed schematic cross-probe registrations.</summary>
[TestClass]
public sealed class SchematicCrossProbeMcpProcessTests
{
    [TestMethod]
    public async Task TypedSchematicCrossProbeToolsAreDiscoverableAndRefuseMalformedTargets()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schematic-cross-probe-stdio-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            string[] names = listed.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!).ToArray();
            CollectionAssert.Contains(names, "kicad_schematic_sync_selection");
            CollectionAssert.Contains(names, "kicad_schematic_highlight_nets");
            CollectionAssert.Contains(names, "kicad_schematic_cross_probe_receipt");

            JsonElement selection = await mcp.Tool("kicad_schematic_sync_selection", new
            {
                instanceId = Guid.NewGuid().ToString("D"), document = new { }, selection = new { },
                expectedStateJson = "{}", operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(selection.GetProperty("isError").GetBoolean(), selection.GetRawText());
            Assert.AreEqual("invalid_schematic_cross_probe_target",
                selection.GetProperty("structuredContent").GetProperty("errorCode").GetString(), selection.GetRawText());

            JsonElement highlight = await mcp.Tool("kicad_schematic_highlight_nets", new
            {
                instanceId = Guid.NewGuid().ToString("D"), document = new { }, highlight = new { },
                expectedStateJson = "{}", operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(highlight.GetProperty("isError").GetBoolean(), highlight.GetRawText());
            Assert.AreEqual("invalid_schematic_cross_probe_target",
                highlight.GetProperty("structuredContent").GetProperty("errorCode").GetString(), highlight.GetRawText());

            JsonElement receipt = await mcp.Tool("kicad_schematic_cross_probe_receipt", new
            {
                instanceId = Guid.NewGuid().ToString("D"), document = new { },
                expectedProcessEpoch = Guid.NewGuid().ToString("D"), operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(receipt.GetProperty("isError").GetBoolean(), receipt.GetRawText());
            Assert.AreEqual("invalid_schematic_cross_probe_target",
                receipt.GetProperty("structuredContent").GetProperty("errorCode").GetString(), receipt.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }
}
