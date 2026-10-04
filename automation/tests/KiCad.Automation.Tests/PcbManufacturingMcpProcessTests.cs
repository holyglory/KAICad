using System.Text.Json;
using System.Reflection;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>
/// Managed black-box checks for the typed manufacturing boundary.  These launch
/// the compiled MCP server over STDIO but never contact KiCad or a native editor.
/// </summary>
[TestClass]
public sealed class PcbManufacturingMcpProcessTests
{
    [TestMethod]
    public void TypedManufacturingInputAcceptsProtobufBoardLayerNamesAndRejectsUnknownLayers()
    {
        const string json = """
            {
              "job": {
                "exportSvg": {
                  "plotSettings": {
                    "layers": ["BL_F_Cu", "BL_F_SilkS", "BL_Edge_Cuts"],
                    "commonLayers": ["F.Cu"]
                  }
                }
              }
            }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PcbManufacturingExportInput input = JsonSerializer.Deserialize<PcbManufacturingExportInput>(json, options)!;
        Assert.IsNotNull(input.Job?.ExportSvg?.PlotSettings);
        CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu, BoardLayer.BlFSilkS, BoardLayer.BlEdgeCuts },
            input.Job.ExportSvg.PlotSettings.Layers);
        CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu }, input.Job.ExportSvg.PlotSettings.CommonLayers);
        Assert.IsNotNull(input.Job.ExportSvg.ToNative().PlotSettings);

        foreach (string spelling in new[] { "BlFCu", "BL_F_Cu", "3" })
        {
            string layerJson = spelling == "3"
                ? "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"layers\":[3]}}}}"
                : $"{{\"job\":{{\"exportSvg\":{{\"plotSettings\":{{\"layers\":[\"{spelling}\"]}}}}}}}}";
            PcbManufacturingExportInput named = JsonSerializer.Deserialize<PcbManufacturingExportInput>(layerJson, options)!;
            CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu }, named.Job!.ExportSvg!.PlotSettings!.Layers, spelling);
        }

        foreach (string invalid in new[]
        {
            "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"layers\":[\"BL_NotALayer\"]}}}}",
            "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"layers\":[2147483647]}}}}"
        })
            Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbManufacturingExportInput>(invalid, options));
    }

    [TestMethod]
    public void EveryTypedManufacturingEnumPropertyAcceptsClrProtobufAndNumericNamesAndRejectsUnknownValues()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Type[] dtoTypes = typeof(PcbManufacturingExportInput).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract
                && (type.Name.StartsWith("RunBoardJobExport", StringComparison.Ordinal)
                    || type == typeof(BoardPlotSettingsInput)))
            .ToArray();
        var enumProperties = dtoTypes.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.PropertyType.IsEnum)
                .Select(property => (Type: type, Property: property)))
            .ToArray();
        Assert.IsTrue(enumProperties.Length > 0, "The typed manufacturing DTOs must expose enum properties.");

        foreach ((Type type, PropertyInfo property) in enumProperties)
        {
            object expected = Enum.GetValues(property.PropertyType).Cast<object>()
                .First(value => Convert.ToInt32(value) != 0);
            string clrName = Enum.GetName(property.PropertyType, expected)!;
            string jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            foreach (object spelling in new object[] { clrName, Convert.ToInt32(expected) })
            {
                string token = spelling is string text ? JsonSerializer.Serialize(text) : spelling.ToString()!;
                object parsed = JsonSerializer.Deserialize($"{{\"{jsonName}\":{token}}}", type, options)!;
                Assert.AreEqual(expected, property.GetValue(parsed), $"{type.Name}.{property.Name} rejected {spelling}.");
            }

            Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(
                $"{{\"{jsonName}\":\"__UNKNOWN_MANUFACTURING_ENUM__\"}}", type, options));
        }
    }

    [TestMethod]
    public void TypedManufacturingInputAcceptsPlotDrillMarkCompatibilityNamesAndRejectsUnknownValues()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach ((string spelling, PlotDrillMarks expected) in new[]
        {
            ("PDM_FULL", PlotDrillMarks.PdmFull),
            ("PdmSmall", PlotDrillMarks.PdmSmall),
            ("full", PlotDrillMarks.PdmFull),
            ("FULL_DRILL_SHAPE", PlotDrillMarks.PdmFull)
        })
        {
            string json = "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"layers\":[\"BL_F_Cu\"],\"commonLayers\":[\"BL_Edge_Cuts\"],\"drillMarks\":\""
                + spelling + "\"},\"pageMode\":\"BJPM_ALL_LAYERS_ONE_PAGE\"}}}";
            PcbManufacturingExportInput input = JsonSerializer.Deserialize<PcbManufacturingExportInput>(json, options)!;
            BoardPlotSettingsInput plotSettings = input.Job!.ExportSvg!.PlotSettings!;
            Assert.AreEqual((PlotDrillMarksInput)expected, plotSettings.DrillMarks, spelling);
            Assert.AreEqual(expected, plotSettings.ToNative().DrillMarks, spelling);
            Assert.AreEqual(BoardJobPaginationModeInput.BjpmAllLayersOnePage, input.Job.ExportSvg.PageMode, spelling);
            Assert.AreEqual((int)BoardJobPaginationMode.BjpmAllLayersOnePage,
                (int)input.Job.ExportSvg.ToNative().PageMode, spelling);
        }

        const string invalid = "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"drillMarks\":\"PDM_NOT_A_MARK\"}}}}";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbManufacturingExportInput>(invalid, options));

        const string invalidPageMode = "{\"job\":{\"exportSvg\":{\"pageMode\":\"BJPM_NOT_A_MODE\"}}}";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbManufacturingExportInput>(invalidPageMode, options));

        const string invalidNumeric = "{\"job\":{\"exportSvg\":{\"plotSettings\":{\"drillMarks\":2147483647},\"pageMode\":2147483647}}}";
        PcbManufacturingExportInput invalidNumericInput = JsonSerializer.Deserialize<PcbManufacturingExportInput>(invalidNumeric, options)!;
        Assert.ThrowsExactly<AutomationException>(() => invalidNumericInput.Job!.ExportSvg!.ToNative());
    }

    [TestMethod]
    public async Task TypedManufacturingToolPublishesJobSchemaAndRejectsMalformedIdentity()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-manufacturing-mcp-schema-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tools = listed.GetProperty("tools");
            JsonElement export = tools.EnumerateArray().Single(tool =>
                tool.GetProperty("name").GetString() == "kicad_pcb_export");

            JsonElement input = export.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
            AssertObject(input, "typed manufacturing input");
            JsonElement inputProperties = input.GetProperty("properties");
            foreach (string property in new[] { "operationId", "document", "expectedState", "outputDirectory", "job" })
                Assert.IsTrue(inputProperties.TryGetProperty(property, out _),
                    $"The typed manufacturing schema is missing {property}.");

            JsonElement job = inputProperties.GetProperty("job");
            AssertObject(job, "manufacturing job envelope");
            JsonElement jobProperties = job.GetProperty("properties");
            foreach (string property in new[]
            {
                "export3D", "exportRender", "exportSvg", "exportDxf", "exportPdf", "exportPs",
                "exportGerbers", "exportDrill", "exportPosition", "exportGencad", "exportIpc2581",
                "exportIpcD356", "exportOdb", "exportStats"
            })
                Assert.IsTrue(jobProperties.TryGetProperty(property, out _),
                    $"The typed manufacturing job schema is missing {property}.");

            JsonElement svg = jobProperties.GetProperty("exportSvg");
            AssertObject(svg, "SVG manufacturing job schema");
            JsonElement svgProperties = svg.GetProperty("properties");
            foreach (string property in new[] { "plotSettings", "pageMode" })
                Assert.IsTrue(svgProperties.TryGetProperty(property, out _),
                    $"The typed SVG job schema is missing {property}.");
            JsonElement plotSettings = svgProperties.GetProperty("plotSettings");
            AssertObject(plotSettings, "SVG plot-settings schema");
            JsonElement plotProperties = plotSettings.GetProperty("properties");
            foreach (string property in new[] { "layers", "commonLayers", "drillMarks" })
                Assert.IsTrue(plotProperties.TryGetProperty(property, out _),
                    $"The typed SVG plot-settings schema is missing {property}.");
            AssertSchemaType(plotProperties.GetProperty("layers"), "array");
            AssertSchemaType(plotProperties.GetProperty("commonLayers"), "array");
            AssertSchemaType(plotProperties.GetProperty("drillMarks"), "integer", "string");
            AssertSchemaType(svgProperties.GetProperty("pageMode"), "integer", "string");

            string processEpoch = Guid.NewGuid().ToString("D");
            string boardPath = Path.Combine(root, "typed.kicad_pcb");
            JsonElement bound = await mcp.Tool("kicad_pcb_export", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new
                {
                    operationId = Guid.NewGuid().ToString("D"),
                    document = new
                    {
                        type = "DOCTYPE_PCB",
                        project = new { name = "typed", path = root },
                        boardFilename = "typed.kicad_pcb"
                    },
                    expectedState = new
                    {
                        document = new
                        {
                            type = "DOCTYPE_PCB",
                            project = new { name = "typed", path = root },
                            boardFilename = "typed.kicad_pcb"
                        },
                        revision = new { epoch = processEpoch, sequence = 1UL },
                        nativeIdentity = Guid.NewGuid().ToString("D"),
                        stateSha256 = new string('0', 64),
                        scope = "DLS_PCB",
                        nativeFiles = new[] { boardPath },
                        fileBaselines = new[]
                        {
                            new
                            {
                                path = boardPath, baselinePath = boardPath, baselineSha256 = "",
                                currentSha256 = "", status = "NFBS_UNCHANGED"
                            }
                        },
                        processEpoch
                    },
                    outputDirectory = Path.Combine(root, "typed-output"),
                    job = new
                    {
                        exportSvg = new
                        {
                            plotSettings = new
                            {
                                layers = new[] { "BL_F_Cu", "BL_F_SilkS" },
                                commonLayers = new[] { "BL_Edge_Cuts" },
                                plotFootprintValues = true,
                                plotReferenceDesignators = true,
                                drillMarks = "PDM_FULL",
                                checkZonesBeforePlot = false
                            },
                            fitPageToBoard = true,
                            precision = 6U,
                            pageMode = "BJPM_ALL_LAYERS_ONE_PAGE"
                        }
                    }
                }
            });
            Assert.IsTrue(bound.GetProperty("isError").GetBoolean(), bound.GetRawText());
            Assert.AreEqual("unknown_instance",
                bound.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
                bound.GetRawText());

            JsonElement malformed = await mcp.Tool("kicad_pcb_export", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new
                {
                    operationId = "not-a-uuid",
                    document = new { },
                    expectedState = new { },
                    outputDirectory = Path.Combine(root, "out"),
                    job = new { exportSvg = new { } }
                }
            });
            Assert.IsTrue(malformed.GetProperty("isError").GetBoolean(), malformed.GetRawText());
            Assert.AreEqual("invalid_operation_id",
                malformed.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
                malformed.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertObject(JsonElement element, string label)
    {
        Assert.IsTrue(element.TryGetProperty("type", out JsonElement type)
            && ((type.ValueKind == JsonValueKind.String && type.GetString() == "object")
                || (type.ValueKind == JsonValueKind.Array
                    && type.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                        && value.GetString() == "object"))),
            $"{label} must expose an object schema: {element.GetRawText()}");
    }

    private static void AssertSchemaType(JsonElement element, params string[] expected)
    {
        Assert.IsTrue((element.TryGetProperty("type", out JsonElement type)
            && (type.ValueKind == JsonValueKind.String
                ? expected.Contains(type.GetString(), StringComparer.Ordinal)
                : type.ValueKind == JsonValueKind.Array
                    && type.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                        && expected.Contains(value.GetString(), StringComparer.Ordinal))))
            || (element.TryGetProperty("enum", out JsonElement values)
                && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0),
            $"Schema type does not include [{string.Join(", ", expected)}]: {element.GetRawText()}");
    }
}
