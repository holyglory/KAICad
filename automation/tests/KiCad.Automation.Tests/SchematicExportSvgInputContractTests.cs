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
    public void TypedExportFamiliesPreserveAllNativeSettings()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "schematic-export-family-contract");
        string rootId = Guid.NewGuid().ToString("D");
        NativeDocumentInput document = new()
        {
            Type = NativeDocumentType.Schematic,
            Project = new() { Name = "family-contract", Path = projectPath },
            SheetPath = new() { Path = [new() { Value = rootId }] }
        };
        SchematicPlotSettingsInput plot = new()
        {
            DrawingSheet = "绘图表",
            DefaultFont = "KiCad 字体",
            Variant = "量产",
            PlotAll = true,
            PlotDrawingSheet = true,
            PlotPages = ["root", "子页"],
            ShowHopOver = true,
            BlackAndWhite = true,
            PageSize = SchematicJobPageSizeInput.A,
            UseBackgroundColor = true,
            MinPenWidth = 11,
            Theme = "dark"
        };
        var dxf = new SchematicExportDxfInput { Document = document, OutputPath = Path.Combine(projectPath, "dxf"), PlotSettings = plot }.ToNative();
        Assert.AreEqual(plot.Theme, dxf.PlotSettings.Theme);
        CollectionAssert.AreEqual(new[] { "root", "子页" }, dxf.PlotSettings.PlotPages);

        var pdf = new SchematicExportPdfInput
        {
            Document = document,
            OutputPath = Path.Combine(projectPath, "out.pdf"),
            PlotSettings = plot,
            PropertyPopups = true,
            HierarchicalLinks = true,
            IncludeMetadata = true
        }.ToNative();
        Assert.IsTrue(pdf.PropertyPopups);
        Assert.IsTrue(pdf.HierarchicalLinks);
        Assert.IsTrue(pdf.IncludeMetadata);

        var ps = new SchematicExportPsInput { Document = document, OutputPath = Path.Combine(projectPath, "ps"), PlotSettings = plot }.ToNative();
        Assert.AreEqual((SchematicJobPageSize)3, ps.PlotSettings.PageSize);

        var netlist = new SchematicExportNetlistInput
        {
            Document = document,
            OutputPath = Path.Combine(projectPath, "net.xml"),
            Format = SchematicNetlistFormatInput.SpiceModel,
            VariantName = "量产"
        }.ToNative();
        Assert.AreEqual(SchematicNetlistFormat.SnfSpiceModel, netlist.Format);
        Assert.AreEqual("量产", netlist.VariantName);

        var bom = new SchematicExportBomInput
        {
            Document = document,
            OutputPath = Path.Combine(projectPath, "bom.csv"),
            Format = new SchematicBomFormatInput
            {
                PresetName = "CSV",
                FieldDelimiter = ",",
                StringDelimiter = "\"",
                RefDelimiter = ";",
                RefRangeDelimiter = "..",
                KeepTabs = true,
                KeepLineBreaks = true,
                IncludeByteOrderMark = true
            },
            Fields = new SchematicBomFieldsInput
            {
                PresetName = "Grouped By Value",
                Fields = [new() { Name = "Value", Label = "Valeur", GroupBy = true }, new() { Name = "Footprint", Label = "Boîtier" }],
                SortField = "Value",
                SortDirection = SchematicBomSortDirectionInput.Descending,
                Filter = "TP",
                FilterScope = SchematicBomFilterScopeInput.All
            },
            ExcludeDnp = true,
            GroupSymbols = true,
            VariantName = "量产"
        }.ToNative();
        Assert.AreEqual("CSV", bom.Format.PresetName);
        Assert.AreEqual(2, bom.Fields.Fields.Count);
        Assert.IsTrue(bom.Fields.Fields[0].GroupBy);
        Assert.AreEqual(BOMSortDirection.BsdDescending, bom.Fields.SortDirection);
        Assert.AreEqual(BOMFilterScope.BfsAll, bom.Fields.FilterScope);
        Assert.IsTrue(bom.ExcludeDnp);
        Assert.IsTrue(bom.GroupSymbols);
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
            JsonElement[] exportTools = listed.GetProperty("tools").EnumerateArray()
                .Where(item => item.GetProperty("name").GetString()?.StartsWith("kicad_schematic_export_", StringComparison.Ordinal) == true
                    && item.GetProperty("name").GetString() is not "kicad_schematic_export_receipt")
                .ToArray();
            CollectionAssert.AreEquivalent(new[] { "svg", "dxf", "pdf", "ps", "netlist", "bom" },
                exportTools.Select(item => item.GetProperty("name").GetString()!.Replace("kicad_schematic_export_", "", StringComparison.Ordinal)).ToArray());
            JsonElement tool = exportTools.Single(item =>
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

            foreach (JsonElement exportTool in exportTools)
            {
                JsonElement exportProperties = exportTool.GetProperty("inputSchema").GetProperty("properties");
                Assert.IsTrue(exportProperties.GetProperty("request").GetProperty("type").EnumerateArray()
                    .Any(item => item.GetString() == "object"), exportTool.GetRawText());
                string name = exportTool.GetProperty("name").GetString()!;
                JsonElement requestConflictForTool = await mcp.Tool(name, new
                {
                    instanceId = Guid.NewGuid().ToString("D"),
                    requestJson = "",
                    request = new { },
                    expectedStateJson = "{}"
                });
                Assert.IsTrue(requestConflictForTool.GetProperty("isError").GetBoolean(), requestConflictForTool.GetRawText());
                Assert.AreEqual("invalid_schematic_export_input", requestConflictForTool.GetProperty("structuredContent")
                    .GetProperty("errorCode").GetString(), requestConflictForTool.GetRawText());

                JsonElement stateConflictForTool = await mcp.Tool(name, new
                {
                    instanceId = Guid.NewGuid().ToString("D"),
                    requestJson = "{}",
                    expectedStateJson = "",
                    expectedState = new { }
                });
                Assert.IsTrue(stateConflictForTool.GetProperty("isError").GetBoolean(), stateConflictForTool.GetRawText());
                Assert.AreEqual("invalid_schematic_export_input", stateConflictForTool.GetProperty("structuredContent")
                    .GetProperty("errorCode").GetString(), stateConflictForTool.GetRawText());
            }

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
