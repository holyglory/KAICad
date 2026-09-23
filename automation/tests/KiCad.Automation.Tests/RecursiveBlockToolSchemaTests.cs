using System.Text.Json;
using KiCad.Automation.Model;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockToolSchemaTests
{
    [TestMethod]
    public void OptionalAnnotationsDoNotBreakTypedMcpSchemaGeneration()
    {
        var tool = McpServerTool.Create((BlockLocalDiagram diagram) => true);
        var schema = tool.ProtocolTool.InputSchema;
        string text = schema.GetRawText();
        StringAssert.Contains(text, "interfaces"); StringAssert.Contains(text, "connections");
        StringAssert.Contains(text, "annotations"); StringAssert.Contains(text, "targetId");
        var defaults = new BlockLocalDiagram([], []);
        Assert.IsFalse(defaults.Annotations.IsDefault);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string json = JsonSerializer.Serialize(defaults, options);
        Assert.IsTrue(defaults.SameContents(JsonSerializer.Deserialize<BlockLocalDiagram>(json, options)!));
        // Existing XML semantics permit omitted optional annotations; JSON input
        // preserves that absence without inventing a note or an electrical object.
        var omitted = JsonSerializer.Deserialize<BlockLocalDiagram>("{\"interfaces\":[],\"connections\":[]}", options)!;
        omitted.Validate(); Assert.IsEmpty(omitted.Notes);
    }
}
