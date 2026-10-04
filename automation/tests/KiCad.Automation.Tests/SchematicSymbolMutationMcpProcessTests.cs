using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>Compiled STDIO contract coverage for typed schematic symbol mutations.</summary>
[TestClass]
public sealed class SchematicSymbolMutationMcpProcessTests
{
    [TestMethod]
    public async Task DiscoverAndRejectMalformedSymbolMutations()
    {
        string root = Directory.CreateTempSubdirectory("kicad-symbol-mutation-mcp-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(UpdateCommandTests.StartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            var listed = await mcp.ListTools(null);
            string[] names = listed.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!).ToArray();
            foreach (string name in new[]
            {
                "kicad_schematic_create_symbol_instance",
                "kicad_schematic_read_symbol_instance",
                "kicad_schematic_update_symbol_instance",
                "kicad_schematic_delete_symbol_instance"
            })
                CollectionAssert.Contains(names, name);

            string instance = Guid.NewGuid().ToString("D"), item = Guid.NewGuid().ToString("D"), operation = Guid.NewGuid().ToString("D");
            var create = await mcp.Tool("kicad_schematic_create_symbol_instance", new
            {
                instanceId = instance, documentJson = "{}", expectedStateJson = "{}",
                symbolJson = "{}", operationId = operation
            });
            Assert.IsTrue(create.GetProperty("isError").GetBoolean(), create.GetRawText());
            Assert.AreEqual("invalid_checked_document",
                create.GetProperty("structuredContent").GetProperty("errorCode").GetString());

            var read = await mcp.Tool("kicad_schematic_read_symbol_instance", new
            {
                instanceId = instance, documentJson = "{}", itemId = item, expectedStateJson = "{}"
            });
            Assert.IsTrue(read.GetProperty("isError").GetBoolean(), read.GetRawText());
            Assert.AreEqual("invalid_checked_document",
                read.GetProperty("structuredContent").GetProperty("errorCode").GetString());

            var update = await mcp.Tool("kicad_schematic_update_symbol_instance", new
            {
                instanceId = instance, documentJson = "{}", itemId = item,
                expectedStateJson = "{}", symbolJson = "{}", operationId = operation
            });
            Assert.IsTrue(update.GetProperty("isError").GetBoolean(), update.GetRawText());
            Assert.AreEqual("invalid_checked_document",
                update.GetProperty("structuredContent").GetProperty("errorCode").GetString());

            var delete = await mcp.Tool("kicad_schematic_delete_symbol_instance", new
            {
                instanceId = instance, documentJson = "{}", itemId = item,
                expectedStateJson = "{}", operationId = operation
            });
            Assert.IsTrue(delete.GetProperty("isError").GetBoolean(), delete.GetRawText());
            Assert.AreEqual("invalid_checked_document",
                delete.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        }
        finally { Directory.Delete(root, true); }
    }
}
