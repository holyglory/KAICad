using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DocumentLifecycleInputContractTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CompiledStdioAdvertisesTypedAndLegacyLifecycleFormsAndRefusesAmbiguity()
    {
        string root = Directory.CreateTempSubdirectory("kicad-document-lifecycle-contract-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                UpdateCommandTests.StartInfo(), Path.Combine(root, "state"),
                Path.Combine(root, "stderr.log"), deadline.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tools = listed.GetProperty("tools");
            foreach (string name in new[]
                     { "kicad_document_state", "kicad_document_save", "kicad_document_close",
                       "kicad_document_revert", "kicad_document_operation" })
                Assert.IsTrue(tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == name),
                    $"The compiled server did not expose {name}.");

            JsonElement save = Find(tools, "kicad_document_save");
            JsonElement saveSchema = save.GetProperty("inputSchema");
            string[] required = saveSchema.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "instanceId", "operationId" }, required);
            JsonElement saveProperties = saveSchema.GetProperty("properties");
            Assert.IsTrue(saveProperties.TryGetProperty("expectedStateJson", out _), save.GetRawText());
            JsonElement expectedState = saveProperties.GetProperty("expectedState");
            Assert.AreEqual("object", expectedState.GetProperty("type").GetString(), save.GetRawText());
            JsonElement expectedProperties = expectedState.GetProperty("properties");
            foreach (string property in new[] { "document", "revision", "nativeIdentity", "stateSha256", "scope",
                                                "nativeFiles", "fileBaselines", "processEpoch" })
                Assert.IsTrue(expectedProperties.TryGetProperty(property, out _),
                    $"The structured lifecycle schema is missing {property}: {save.GetRawText()}");

            JsonElement document = expectedProperties.GetProperty("document");
            JsonElement documentProperties = document.GetProperty("properties");
            foreach (string property in new[] { "type", "project", "sheetPath", "libId", "boardFilename" })
                Assert.IsTrue(documentProperties.TryGetProperty(property, out _),
                    $"The structured document schema is missing {property}: {save.GetRawText()}");

            JsonElement operation = Find(tools, "kicad_document_operation");
            JsonElement operationProperties = operation.GetProperty("inputSchema").GetProperty("properties");
            foreach (string property in new[] { "instanceId", "operationId", "processEpoch", "documentJson", "document" })
                Assert.IsTrue(operationProperties.TryGetProperty(property, out _),
                    $"The operation schema is missing {property}: {operation.GetRawText()}");

            string instanceId = Guid.NewGuid().ToString("D");
            string operationId = Guid.NewGuid().ToString("D");
            JsonElement conflict = await mcp.Tool("kicad_document_save", new
            {
                instanceId,
                operationId,
                expectedStateJson = "",
                expectedState = new { }
            });
            Assert.IsTrue(conflict.GetProperty("isError").GetBoolean(), conflict.GetRawText());
            Assert.AreEqual("invalid_lifecycle_input",
                conflict.GetProperty("structuredContent").GetProperty("code").GetString(), conflict.GetRawText());

            JsonElement missingState = await mcp.Tool("kicad_document_state", new { instanceId });
            Assert.IsTrue(missingState.GetProperty("isError").GetBoolean(), missingState.GetRawText());
            Assert.AreEqual("invalid_document_input",
                missingState.GetProperty("structuredContent").GetProperty("code").GetString(), missingState.GetRawText());
        }
        finally
        {
            string log = Path.Combine(root, "stderr.log");
            if (File.Exists(log))
            {
                string retained = Path.Combine(TestContext.TestResultsDirectory!, "document-lifecycle-input-mcp.log");
                File.Copy(log, retained, true);
                TestContext.AddResultFile(retained);
            }
            Directory.Delete(root, true);
        }
    }

    private static JsonElement Find(JsonElement tools, string name) => tools.EnumerateArray()
        .Single(tool => tool.GetProperty("name").GetString() == name);
}
