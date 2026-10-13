using System.Text.Json;
using Google.Protobuf;
using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRender3dTypedMcpProcessTests
{
    [TestMethod]
    public void TypedRender3dInputBindsLifecycleDocumentAndCameraSettings()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-render-3d-input-").FullName;
        try
        {
            var request = CheckedSchematicToolTests.Request(root, Guid.NewGuid().ToString("D"));
            request.ExpectedState.Document.Type = DocumentType.DoctypePcb;
            request.ExpectedState.Document.BoardFilename = "fixture.kicad_pcb";
            request.ExpectedState.Scope = DocumentLifecycleScope.DlsPcb;
            request.Batch.Document = request.ExpectedState.Document.Clone();

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var input = JsonSerializer.Deserialize<PcbRender3dTypedInput>(JsonSerializer.Serialize(new
            {
                document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.Batch.Document)),
                expectedState = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.ExpectedState)),
                request = new
                {
                    format = 0,
                    quality = 1,
                    backgroundStyle = 3,
                    width = 320,
                    height = 240,
                    side = 1,
                    zoom = 1.25,
                    perspective = true,
                    appearancePreset = "studio"
                }
            }, options), options)!;

            var payload = input.ToPayload();
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(payload.Document);
            var state = SchematicJson.Parser.Parse<DocumentLifecycleState>(payload.ExpectedState);
            var render = SchematicJson.Parser.Parse<RunBoardJobExportRender>(payload.Request);
            Assert.AreEqual(DocumentType.DoctypePcb, document.Type);
            Assert.AreEqual(document, state.Document);
            Assert.AreEqual(document, render.JobSettings.Document);
            Assert.AreEqual(320, render.Width);
            Assert.AreEqual(240, render.Height);
            Assert.AreEqual(RenderSide.RsTop, render.Side);
            Assert.AreEqual(1.25, render.Zoom);
            Assert.IsTrue(render.Perspective);
            Assert.AreEqual("studio", render.AppearancePreset);
            Assert.AreEqual(RenderFormat.RfUnknown, render.Format,
                "The service selects PNG internally and owns the temporary output path.");
            Assert.AreEqual(string.Empty, render.JobSettings.OutputPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void TypedRender3dInputRejectsJpegAndOutOfRangeDimensionsBeforeNativeContact()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-render-3d-invalid-").FullName;
        try
        {
            var request = CheckedSchematicToolTests.Request(root, Guid.NewGuid().ToString("D"));
            request.ExpectedState.Document.Type = DocumentType.DoctypePcb;
            request.ExpectedState.Document.BoardFilename = "fixture.kicad_pcb";
            request.ExpectedState.Scope = DocumentLifecycleScope.DlsPcb;
            request.Batch.Document = request.ExpectedState.Document.Clone();
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            foreach (object render in new object[]
            {
                new { format = 2, quality = 1, backgroundStyle = 3, width = 320, height = 240, side = 1, zoom = 1.0 },
                new { format = 0, quality = 1, backgroundStyle = 3, width = 32, height = 240, side = 1, zoom = 1.0 },
                new { format = 0, quality = 1, backgroundStyle = 3, width = 320, height = 240, side = 1, zoom = 0.0 }
            })
            {
                var input = JsonSerializer.Deserialize<PcbRender3dTypedInput>(JsonSerializer.Serialize(new
                {
                    document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.Batch.Document)),
                    expectedState = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.ExpectedState)),
                    request = render
                }, options), options)!;
                var error = Assert.ThrowsExactly<AutomationException>(() => input.ToPayload());
                Assert.AreEqual("invalid_pcb_render_3d_typed_input", error.Code);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task TypedRender3dSchemaAndIncompleteInputRefusalAreVisibleOverStdio()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-render-3d-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "kicad_pcb_render_3d_geometry_typed");
            JsonElement input = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
            JsonElement properties = input.GetProperty("properties");
            foreach (string name in new[] { "document", "expectedState", "request" })
                Assert.IsTrue(properties.TryGetProperty(name, out _), name);
            foreach (string name in new[] { "format", "quality", "backgroundStyle", "width", "height", "side", "zoom", "perspective" })
                Assert.IsTrue(properties.GetProperty("request").GetProperty("properties").TryGetProperty(name, out _), name);

            JsonElement refusal = await mcp.Tool("kicad_pcb_render_3d_geometry_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"), input = new { }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_pcb_render_3d_typed_input",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
