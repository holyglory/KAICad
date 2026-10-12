using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbZoneTypedMcpProcessTests
{
    [TestMethod]
    public void TypedZoneMutationInputRejectsUnknownMutationKind()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PcbZoneMutationInput input = JsonSerializer.Deserialize<PcbZoneMutationInput>("""
            { "mutation": "create", "zone": { "id": "11111111-1111-1111-1111-111111111111" } }
            """, options)!;
        Assert.AreEqual(PcbZoneMutationKind.Create, input.Mutation);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbZoneMutationInput>(
            "{\"mutation\":\"not-a-zone-operation\"}", options));
    }

    [TestMethod]
    public async Task TypedZoneMutationPublishesStructuredSchemaAndRejectsIncompleteInput()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-zone-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "kicad_pcb_zone_mutation_typed");
            JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");
            JsonElement input = properties.GetProperty("input");
            foreach (string property in new[] { "document", "expectedState", "mutation", "operationId", "zoneId", "zone" })
                Assert.IsTrue(input.GetProperty("properties").TryGetProperty(property, out _), property);

            JsonElement refusal = await mcp.Tool("kicad_pcb_zone_mutation_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"), input = new { }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_pcb_zone_typed_input",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
