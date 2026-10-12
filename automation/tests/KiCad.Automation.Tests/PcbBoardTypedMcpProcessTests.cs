using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>
/// Managed contract checks for the structured PCB inspection boundary.  The
/// malformed call deliberately stops before a native instance is needed.
/// </summary>
[TestClass]
public sealed class PcbBoardTypedMcpProcessTests
{
    [TestMethod]
    public void TypedPcbQueryInputUsesExplicitQueryAndRejectsUnknownKinds()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PcbBoardQueryInput input = JsonSerializer.Deserialize<PcbBoardQueryInput>("""
            {
              "document": {
                "type": "DOCTYPE_PCB",
                "project": { "name": "demo", "path": "/tmp/demo" },
                "boardFilename": "demo.kicad_pcb"
              },
              "query": "itemsByNet",
              "nets": ["VCC"]
            }
            """, options)!;

        Assert.AreEqual(PcbBoardQueryKind.ItemsByNet, input.Query);
        CollectionAssert.AreEqual(new[] { "VCC" }, input.Nets);
        Assert.IsNotNull(input.Document);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbBoardQueryInput>("""
            { "query": "not-a-board-query" }
            """, options));
    }

    [TestMethod]
    public async Task TypedPcbQueryPublishesStructuredSchemaAndRejectsIncompleteState()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-query-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "kicad_pcb_query_typed");
            JsonElement inputSchema = tool.GetProperty("inputSchema");
            Assert.IsTrue(inputSchema.GetProperty("properties").TryGetProperty("input", out JsonElement input),
                inputSchema.GetRawText());
            Assert.IsTrue(input.GetProperty("properties").TryGetProperty("query", out _), input.GetRawText());
            Assert.IsTrue(input.GetProperty("properties").TryGetProperty("expectedState", out _), input.GetRawText());

            JsonElement refusal = await mcp.Tool("kicad_pcb_query_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new
                {
                    document = new
                    {
                        type = "DOCTYPE_PCB",
                        project = new { name = "demo", path = root },
                        boardFilename = "demo.kicad_pcb"
                    },
                    query = "itemsByNet",
                    nets = new[] { "VCC" }
                }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_lifecycle_state",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
