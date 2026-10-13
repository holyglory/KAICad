using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbKeepoutTypedMcpProcessTests
{
    [TestMethod]
    public void TypedKeepoutMutationInputRejectsUnknownMutationKind()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PcbKeepoutMutationInput input = JsonSerializer.Deserialize<PcbKeepoutMutationInput>("""
            { "mutation": "create", "keepout": { "id": "11111111-1111-1111-1111-111111111111" } }
            """, options)!;
        Assert.AreEqual(PcbKeepoutMutationKind.Create, input.Mutation);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbKeepoutMutationInput>(
            "{\"mutation\":\"not-a-keepout-operation\"}", options));
    }

    [TestMethod]
    public async Task TypedKeepoutMutationPublishesStructuredSchemaAndRejectsIncompleteInput()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-keepout-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "kicad_pcb_keepout_mutation_typed");
            JsonElement input = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
            foreach (string property in new[] { "document", "expectedState", "mutation", "operationId", "keepoutId", "keepout" })
                Assert.IsTrue(input.GetProperty("properties").TryGetProperty(property, out _), property);

            JsonElement refusal = await mcp.Tool("kicad_pcb_keepout_mutation_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"), input = new { }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_pcb_keepout_typed_input",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
