using System.Text.Json;

namespace KiCad.Automation.Tests;

/// <summary>Compiled STDIO contract coverage for typed project schematic metadata edits.</summary>
[TestClass]
public sealed class SchematicMetadataMutationMcpProcessTests
{
    [TestMethod]
    public async Task DiscoverAndRejectMalformedMetadataMutations()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schematic-metadata-mcp-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(UpdateCommandTests.StartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            var listed = await mcp.ListTools(null);
            string[] names = listed.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!).ToArray();
            CollectionAssert.Contains(names, "kicad_schematic_replace_bus_aliases");
            CollectionAssert.Contains(names, "kicad_schematic_replace_variant_registry");

            string instance = Guid.NewGuid().ToString("D");
            var aliases = await mcp.Tool("kicad_schematic_replace_bus_aliases", new
            {
                instanceId = instance, documentJson = "{}", expectedStateJson = "{}",
                aliases = Array.Empty<object>(), operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(aliases.GetProperty("isError").GetBoolean(), aliases.GetRawText());
            Assert.AreEqual("invalid_document_target",
                aliases.GetProperty("structuredContent").GetProperty("errorCode").GetString());

            var variants = await mcp.Tool("kicad_schematic_replace_variant_registry", new
            {
                instanceId = instance, documentJson = "{}", expectedStateJson = "{}",
                entries = Array.Empty<object>(), operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(variants.GetProperty("isError").GetBoolean(), variants.GetRawText());
            Assert.AreEqual("invalid_document_target",
                variants.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        }
        finally { Directory.Delete(root, true); }
    }
}
