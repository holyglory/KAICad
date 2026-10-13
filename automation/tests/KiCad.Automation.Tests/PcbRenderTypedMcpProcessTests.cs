using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRenderTypedMcpProcessTests
{
    [TestMethod]
    public void TypedGeometryInputBindsCompositeSelectorsAndRejectsUnknownMembers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PcbRenderGeometryTypedInput input = JsonSerializer.Deserialize<PcbRenderGeometryTypedInput>("""
            {
              "document": {
                "type": "DOCTYPE_PCB",
                "project": { "name": "demo", "path": "/tmp/demo" },
                "boardFilename": "demo.kicad_pcb"
              },
              "request": {
                "viewport": { "minXNm": 0, "minYNm": 0, "maxXNm": 1000000, "maxYNm": 1000000 },
                "width": 800,
                "height": 600,
                "layers": ["F.Cu", "B.Cu"],
                "objectTypes": ["track", "pad"],
                "includeGeometry": true,
                "maxObjects": 512
              }
            }
            """, options)!;

        Assert.IsNotNull(input.Document);
        Assert.IsNotNull(input.Request);
        CollectionAssert.AreEqual(new[] { "F.Cu", "B.Cu" }, input.Request!.Layers);
        CollectionAssert.AreEqual(new[] { "track", "pad" }, input.Request.ObjectTypes);
        Assert.AreEqual(800, input.Request.Width);
        Assert.AreEqual(600, input.Request.Height);

        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbRenderGeometryTypedInput>("""
            { "request": { "unknown": true } }
            """, options));
    }

    [TestMethod]
    public async Task TypedGeometrySchemaAndIncompleteInputRefusalAreVisibleOverStdio()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-render-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);

            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "kicad_pcb_render_geometry_typed");
            JsonElement input = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
            JsonElement inputProperties = input.GetProperty("properties");
            foreach (string property in new[] { "document", "expectedState", "request" })
                Assert.IsTrue(inputProperties.TryGetProperty(property, out _), property);

            JsonElement request = inputProperties.GetProperty("request");
            JsonElement requestProperties = request.GetProperty("properties");
            foreach (string property in new[] { "viewport", "width", "height", "layers", "objectTypes", "includeGeometry", "maxObjects" })
                Assert.IsTrue(requestProperties.TryGetProperty(property, out _), property);

            JsonElement refusal = await mcp.Tool("kicad_pcb_render_geometry_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new { }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_pcb_render_geometry_typed_input",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
