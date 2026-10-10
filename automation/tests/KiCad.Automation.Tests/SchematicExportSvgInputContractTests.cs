using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic.Jobs;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicExportSvgInputContractTests
{
    [TestMethod]
    public void TypedSvgInputPreservesDocumentPlotSettingsAndRepeatedPages()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "schematic-svg-contract");
        string rootId = Guid.NewGuid().ToString("D");
        var input = new SchematicExportSvgInput
        {
            Document = new NativeDocumentInput
            {
                Type = NativeDocumentType.Schematic,
                Project = new() { Name = "svg-contract", Path = projectPath },
                SheetPath = new() { Path = [new() { Value = rootId }] }
            },
            OutputPath = Path.Combine(projectPath, "out"),
            PlotSettings = new()
            {
                DrawingSheet = "contract-sheet",
                DefaultFont = "KiCad Font",
                Variant = "prototype",
                PlotAll = true,
                PlotDrawingSheet = false,
                PlotPages = ["root", "child"],
                ShowHopOver = true,
                BlackAndWhite = true,
                PageSize = SchematicJobPageSizeInput.A4,
                UseBackgroundColor = true,
                MinPenWidth = 7,
                Theme = "dark"
            }
        };

        RunSchematicJobExportSvg native = input.ToNative();
        Assert.AreEqual(DocumentType.DoctypeSchematic, native.JobSettings.Document.Type);
        Assert.AreEqual(projectPath, native.JobSettings.Document.Project.Path);
        Assert.AreEqual(rootId, native.JobSettings.Document.SheetPath.Path.Single().Value);
        Assert.AreEqual(input.OutputPath, native.JobSettings.OutputPath);
        Assert.AreEqual("contract-sheet", native.PlotSettings.DrawingSheet);
        Assert.AreEqual("KiCad Font", native.PlotSettings.DefaultFont);
        Assert.AreEqual("prototype", native.PlotSettings.Variant);
        Assert.IsTrue(native.PlotSettings.PlotAll);
        Assert.IsFalse(native.PlotSettings.PlotDrawingSheet);
        CollectionAssert.AreEqual(new[] { "root", "child" }, native.PlotSettings.PlotPages);
        Assert.IsTrue(native.PlotSettings.ShowHopOver);
        Assert.IsTrue(native.PlotSettings.BlackAndWhite);
        Assert.AreEqual((SchematicJobPageSize)2, native.PlotSettings.PageSize);
        Assert.IsTrue(native.PlotSettings.UseBackgroundColor);
        Assert.AreEqual(7, native.PlotSettings.MinPenWidth);
        Assert.AreEqual("dark", native.PlotSettings.Theme);
    }

    [TestMethod]
    public void TypedSvgInputRejectsUnknownAndDuplicateMembers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SchematicExportSvgInput>(
            "{\"outputPath\":\"/tmp/a\",\"outputPath\":\"/tmp/b\"}", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SchematicExportSvgInput>(
            "{\"unexpected\":true}", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SchematicPlotSettingsInput>(
            "{\"pageSize\":\"SJPS_NOT_DEFINED\"}", options));
    }

    [TestMethod]
    public async Task StdioSchemaExposesTypedSvgInputAndRefusesConflictingFormsBeforeNativeContact()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schematic-svg-schema-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "kicad_schematic_export_svg");
            JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");
            foreach (string property in new[] { "instanceId", "requestJson", "request", "expectedStateJson", "expectedState", "operationId" })
                Assert.IsTrue(properties.TryGetProperty(property, out _), property);
            Assert.IsTrue(properties.GetProperty("request").GetProperty("type").EnumerateArray().Any(item => item.GetString() == "object"));
            string requestSchema = properties.GetProperty("request").GetRawText();
            foreach (string member in new[] { "document", "outputPath", "plotSettings" })
                StringAssert.Contains(requestSchema, "\"" + member + "\"", requestSchema);
            foreach (string member in new[] { "drawingSheet", "defaultFont", "variant", "plotAll", "plotDrawingSheet",
                         "plotPages", "showHopOver", "blackAndWhite", "pageSize", "useBackgroundColor", "minPenWidth", "theme" })
                StringAssert.Contains(requestSchema, "\"" + member + "\"", requestSchema);

            JsonElement requestConflict = await mcp.Tool("kicad_schematic_export_svg", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                requestJson = "",
                request = new { outputPath = Path.Combine(root, "svg") },
                expectedStateJson = "{}"
            });
            Assert.IsTrue(requestConflict.GetProperty("isError").GetBoolean(), requestConflict.GetRawText());
            Assert.AreEqual("invalid_schematic_export_input", requestConflict.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), requestConflict.GetRawText());

            JsonElement stateConflict = await mcp.Tool("kicad_schematic_export_svg", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                requestJson = "{}",
                expectedStateJson = "",
                expectedState = new { }
            });
            Assert.IsTrue(stateConflict.GetProperty("isError").GetBoolean(), stateConflict.GetRawText());
            Assert.AreEqual("invalid_schematic_export_input", stateConflict.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), stateConflict.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }
}
