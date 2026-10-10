using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Jobs;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicExportSvgInputContractTests
{
    [TestMethod]
    public async Task TypedAndLegacyExportsDispatchEquivalentJobsAndReplayPersistedReceipts()
    {
        string directory = Directory.CreateTempSubdirectory("schematic-export-dispatch-contract-").FullName;
        string epoch = Guid.NewGuid().ToString("D");
        string instanceId = Guid.NewGuid().ToString("D");
        var transport = new SchematicExportTransport(instanceId, epoch);
        var registry = new InstanceRegistry(transport, directory);
        await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), instanceId);
        var tools = new SchematicJobTools(registry);
        DocumentLifecycleState state = CleanState(directory, epoch);
        transport.State = state;
        string legacyState = SchematicJson.Formatter.Format(state);

        try
        {
            var cases = BuildExportCases(directory, state, instanceId);
            int expectedJobs = 0;
            foreach (ExportCase export in cases)
            {
                string operationId = export.OperationId;
                CallToolResult typed = await export.Typed(tools, operationId, TypedState(state), default);
                AssertSuccess(typed, export.Kind);
                AssertManifest(typed, state, export.ExpectedOutputPath);
                Assert.AreEqual(++expectedJobs, transport.JobPayloads.Count, export.Kind);

                CallToolResult replay = await export.Typed(tools, operationId, TypedState(state), default);
                AssertSuccess(replay, export.Kind + " replay");
                Assert.AreEqual(expectedJobs, transport.JobPayloads.Count, export.Kind + " replay must not redispatch");

                string legacyRequest = export.LegacyRequestJson;
                CallToolResult legacy = await export.Legacy(tools, legacyRequest, legacyState, Guid.NewGuid().ToString("D"), default);
                AssertSuccess(legacy, export.Kind + " legacy");
                AssertManifest(legacy, state, export.ExpectedOutputPath);
                Assert.AreEqual(++expectedJobs, transport.JobPayloads.Count, export.Kind + " legacy dispatch");
                CollectionAssert.AreEqual(transport.JobPayloads[^2], transport.JobPayloads[^1],
                    export.Kind + " typed and canonical legacy payloads differ");
            }

            Assert.AreEqual(cases.Count * 2, transport.JobPayloads.Count);
            Assert.AreEqual(cases.Count * 4, transport.LifecycleReads,
                "Every typed and legacy export reads the exact lifecycle checkpoint before and after dispatch.");
            Assert.IsTrue(transport.Requests.All(request => request.Message.Is(GetAutomationSession.Descriptor)
                || request.Message.Is(ReadDocumentLifecycleState.Descriptor)
                || request.Message.Is(RunSchematicJobExportSvg.Descriptor)
                || request.Message.Is(RunSchematicJobExportDxf.Descriptor)
                || request.Message.Is(RunSchematicJobExportPdf.Descriptor)
                || request.Message.Is(RunSchematicJobExportPs.Descriptor)
                || request.Message.Is(RunSchematicJobExportNetlist.Descriptor)
                || request.Message.Is(RunSchematicJobExportBOM.Descriptor)));

            // Receipt recovery uses a fresh tool and registry. The new peer is
            // intentionally disconnected; replay must be served from MCP state.
            var recovered = new SchematicJobTools(new InstanceRegistry(
                new SchematicExportTransport(instanceId, epoch), directory));
            CallToolResult recoveredReplay = await cases[0].Typed(recovered,
                cases[0].OperationId, TypedState(state), default);
            AssertSuccess(recoveredReplay, "svg persisted replay");
            Assert.AreEqual(expectedJobs, transport.JobPayloads.Count, "Persisted replay contacted no native peer.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cases[0].Typed(tools,
                Guid.NewGuid().ToString("D"), TypedState(state), cancelled.Token));
            Assert.AreEqual(expectedJobs, transport.JobPayloads.Count,
                "Cancellation before dispatch must not reach the fixture peer.");
            Assert.AreEqual(state, transport.LastLifecycleState);
        }
        finally { Directory.Delete(directory, true); }
    }

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

    private sealed record ExportCase(
        string Kind,
        string ExpectedOutputPath,
        string LegacyRequestJson,
        string OperationId,
        Func<SchematicJobTools, string, DocumentLifecycleInput, CancellationToken, Task<CallToolResult>> Typed,
        Func<SchematicJobTools, string, string, string, CancellationToken, Task<CallToolResult>> Legacy);

    private static IReadOnlyList<ExportCase> BuildExportCases(string directory, DocumentLifecycleState state, string instanceId)
    {
        NativeDocumentInput Document() => new()
        {
            Type = NativeDocumentType.Schematic,
            Project = new() { Name = state.Document.Project.Name, Path = state.Document.Project.Path },
            SheetPath = new() { Path = [new() { Value = state.Document.SheetPath.Path.Single().Value }] }
        };
        SchematicPlotSettingsInput Plot() => new()
        {
            DrawingSheet = "sheet-✓",
            DefaultFont = "KiCad 字体",
            Variant = "量产",
            PlotAll = true,
            PlotDrawingSheet = false,
            PlotPages = ["root", "子页"],
            ShowHopOver = true,
            BlackAndWhite = true,
            PageSize = SchematicJobPageSizeInput.A4,
            UseBackgroundColor = true,
            MinPenWidth = 5,
            Theme = "dark"
        };
        var result = new List<ExportCase>();

        void Add(string kind, string output, IMessage native, string operationId,
            Func<SchematicJobTools, string, DocumentLifecycleInput, CancellationToken, Task<CallToolResult>> typed,
            Func<SchematicJobTools, string, string, string, CancellationToken, Task<CallToolResult>> legacy)
            => result.Add(new(kind, output, SchematicJson.Formatter.Format(native), operationId, typed, legacy));

        string svgOutput = Path.Combine(directory, "typed-svg");
        var svgInput = new SchematicExportSvgInput { Document = Document(), OutputPath = svgOutput, PlotSettings = Plot() };
        Add("svg", svgOutput, svgInput.ToNative(), Guid.NewGuid().ToString("D"),
            (tools, operation, checkpoint, token) => tools.ExportSvgStructured(instanceId, token,
                request: svgInput, expectedState: checkpoint, operationId: operation),
            (tools, request, stateJson, operation, token) => tools.ExportSvg(instanceId, request, stateJson, token, operation));

        string dxfOutput = Path.Combine(directory, "typed-dxf");
        var dxfInput = new SchematicExportDxfInput { Document = Document(), OutputPath = dxfOutput, PlotSettings = Plot() };
        Add("dxf", dxfOutput, dxfInput.ToNative(), Guid.NewGuid().ToString("D"),
            (tools, operation, checkpoint, token) => tools.ExportDxfStructured(instanceId, token,
                request: dxfInput, expectedState: checkpoint, operationId: operation),
            (tools, request, stateJson, operation, token) => tools.ExportDxf(instanceId, request, stateJson, token, operation));

        string pdfOutput = Path.Combine(directory, "typed-pdf", "drawing.pdf");
        var pdfInput = new SchematicExportPdfInput
        {
            Document = Document(), OutputPath = pdfOutput, PlotSettings = Plot(),
            PropertyPopups = true, HierarchicalLinks = true, IncludeMetadata = true
        };
        Add("pdf", pdfOutput, pdfInput.ToNative(), Guid.NewGuid().ToString("D"),
            (tools, operation, checkpoint, token) => tools.ExportPdfStructured(instanceId, token,
                request: pdfInput, expectedState: checkpoint, operationId: operation),
            (tools, request, stateJson, operation, token) => tools.ExportPdf(instanceId, request, stateJson, token, operation));

        string psOutput = Path.Combine(directory, "typed-ps");
        var psInput = new SchematicExportPsInput { Document = Document(), OutputPath = psOutput, PlotSettings = Plot() };
        Add("ps", psOutput, psInput.ToNative(), Guid.NewGuid().ToString("D"),
            (tools, operation, checkpoint, token) => tools.ExportPsStructured(instanceId, token,
                request: psInput, expectedState: checkpoint, operationId: operation),
            (tools, request, stateJson, operation, token) => tools.ExportPs(instanceId, request, stateJson, token, operation));

        string netlistOutput = Path.Combine(directory, "typed-netlist", "netlist.xml");
        var netlistInput = new SchematicExportNetlistInput
        {
            Document = Document(), OutputPath = netlistOutput,
            Format = SchematicNetlistFormatInput.KicadXml, VariantName = "量产"
        };
        Add("netlist", netlistOutput, netlistInput.ToNative(), Guid.NewGuid().ToString("D"),
            (tools, operation, checkpoint, token) => tools.ExportNetlistStructured(instanceId, token,
                request: netlistInput, expectedState: checkpoint, operationId: operation),
            (tools, request, stateJson, operation, token) => tools.ExportNetlist(instanceId, request, stateJson, token, operation));

        string bomOutput = Path.Combine(directory, "typed-bom", "bom.csv");
        var bomInput = new SchematicExportBomInput
        {
            Document = Document(), OutputPath = bomOutput,
            Format = new SchematicBomFormatInput
            {
                PresetName = "CSV", FieldDelimiter = ",", StringDelimiter = "\"",
                RefDelimiter = ";", RefRangeDelimiter = "..", KeepTabs = true,
                KeepLineBreaks = true, IncludeByteOrderMark = true
            },
            Fields = new SchematicBomFieldsInput
            {
                PresetName = "Grouped By Value",
                Fields = [new() { Name = "Value", Label = "Valeur", GroupBy = true }],
                SortField = "Value", SortDirection = SchematicBomSortDirectionInput.Ascending,
                Filter = "TP", FilterScope = SchematicBomFilterScopeInput.Visible
            },
            ExcludeDnp = true, GroupSymbols = true, VariantName = "量产"
        };
        Add("bom", bomOutput, bomInput.ToNative(), Guid.NewGuid().ToString("D"),
            (tools, operation, checkpoint, token) => tools.ExportBomStructured(instanceId, token,
                request: bomInput, expectedState: checkpoint, operationId: operation),
            (tools, request, stateJson, operation, token) => tools.ExportBom(instanceId, request, stateJson, token, operation));
        return result;
    }

    private static void AssertSuccess(CallToolResult result, string context)
    {
        Assert.IsFalse(result.IsError ?? false, context + ": " + result.StructuredContent?.GetRawText());
        Assert.IsNotNull(result.StructuredContent, context);
    }

    private static void AssertManifest(CallToolResult result, DocumentLifecycleState state, string expectedPath)
    {
        JsonElement data = result.StructuredContent!.Value;
        string stateJson = SchematicJson.Formatter.Format(state);
        Assert.AreEqual(stateJson, data.GetProperty("beforeState").GetString());
        Assert.AreEqual(stateJson, data.GetProperty("afterState").GetString());
        Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean());
        Assert.IsFalse(data.GetProperty("schematicMutation").GetBoolean());
        foreach (JsonElement output in data.GetProperty("outputs").EnumerateArray())
        {
            string path = output.GetProperty("path").GetString()!;
            Assert.IsTrue(File.Exists(path));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.AreEqual(bytes.LongLength, output.GetProperty("bytes").GetInt64());
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), output.GetProperty("sha256").GetString());
            if (Directory.Exists(expectedPath))
                StringAssert.StartsWith(path, Path.GetFullPath(expectedPath) + Path.DirectorySeparatorChar);
            else
                Assert.AreEqual(Path.GetFullPath(expectedPath), path);
        }
    }

    private static DocumentLifecycleState CleanState(string directory, string epoch)
    {
        string identity = Guid.NewGuid().ToString("D");
        string schematic = Path.Combine(directory, "fixture.kicad_sch");
        string project = Path.Combine(directory, "fixture.kicad_pro");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSchematic,
            Project = new() { Name = "fixture", Path = directory },
            SheetPath = new() { Path = { new KIID { Value = Guid.NewGuid().ToString("D") } } }
        };
        return new()
        {
            Document = document,
            ProcessEpoch = epoch,
            NativeIdentity = identity,
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 9 },
            StateSha256 = new string('a', 64),
            Scope = DocumentLifecycleScope.DlsSchematicHierarchy,
            NativeContentDirty = false,
            ProjectSettingsIncluded = true,
            CompleteChangeTracking = true,
            DiskBaselineChecked = true,
            CleanCheckpointSha256 = new string('b', 64),
            SaveStableStateSha256 = new string('c', 64),
            NativeFiles = { schematic, project },
            FileBaselines =
            {
                Baseline(schematic, 'd'),
                Baseline(project, 'e')
            }
        };
    }

    private static NativeFileBaselineState Baseline(string path, char marker) => new()
    {
        Path = path, BaselinePath = path, BaselineKnown = true, BaselineExists = true,
        BaselineSha256 = new string(marker, 64), BaselineBytes = 10,
        CurrentKnown = true, CurrentExists = true, CurrentSha256 = new string(marker, 64), CurrentBytes = 10,
        Status = NativeFileBaselineStatus.NfbsUnchanged
    };

    private static DocumentLifecycleInput TypedState(DocumentLifecycleState state) => new()
    {
        Document = new NativeDocumentInput
        {
            Type = NativeDocumentType.Schematic,
            Project = new() { Name = state.Document.Project.Name, Path = state.Document.Project.Path },
            SheetPath = new() { Path = [new() { Value = state.Document.SheetPath.Path.Single().Value }] }
        },
        Revision = new() { Epoch = state.Revision.Epoch, Sequence = state.Revision.Sequence },
        NativeIdentity = state.NativeIdentity,
        StateSha256 = state.StateSha256,
        Scope = DocumentLifecycleScopeInput.SchematicHierarchy,
        NativeContentDirty = state.NativeContentDirty,
        ProjectSettingsIncluded = state.ProjectSettingsIncluded,
        CompleteChangeTracking = state.CompleteChangeTracking,
        DiskBaselineChecked = state.DiskBaselineChecked,
        NativeFiles = state.NativeFiles.ToList(),
        FileBaselines = state.FileBaselines.Select(file => new NativeFileBaselineInput
        {
            Path = file.Path, BaselinePath = file.BaselinePath,
            BaselineKnown = file.BaselineKnown, BaselineExists = file.BaselineExists,
            BaselineSha256 = file.BaselineSha256, BaselineBytes = file.BaselineBytes,
            CurrentKnown = file.CurrentKnown, CurrentExists = file.CurrentExists,
            CurrentSha256 = file.CurrentSha256, CurrentBytes = file.CurrentBytes,
            Status = NativeFileBaselineStatusInput.Unchanged
        }).Cast<NativeFileBaselineInput?>().ToList(),
        ProcessEpoch = state.ProcessEpoch,
        CleanCheckpointSha256 = state.CleanCheckpointSha256,
        SaveStableStateSha256 = state.SaveStableStateSha256
    };

    private sealed class SchematicExportTransport : INativeTransport
    {
        public readonly List<ApiRequest> Requests = [];
        public readonly List<byte[]> JobPayloads = [];
        public int LifecycleReads { get; private set; }
        public DocumentLifecycleState? LastLifecycleState { get; private set; }
        public DocumentLifecycleState State { set => state = value.Clone(); }
        private readonly string instanceId;
        private readonly string epoch;
        private DocumentLifecycleState? state;

        public SchematicExportTransport(string instanceId, string epoch)
        {
            this.instanceId = instanceId;
            this.epoch = epoch;
        }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ApiRequest envelope = ApiRequest.Parser.ParseFrom(request);
            Requests.Add(envelope);
            Any message = envelope.Message;
            if (message.Is(GetAutomationSession.Descriptor))
                return Reply(new AutomationSession { ProtocolVersion = 1, InstanceId = instanceId,
                    ProjectPath = "/fixture/fixture.kicad_pro", Epoch = epoch });
            if (message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                ++LifecycleReads;
                LastLifecycleState = state?.Clone();
                return Reply(state!.Clone());
            }

            if (message.Is(RunSchematicJobExportSvg.Descriptor))
                return Run(message.Unpack<RunSchematicJobExportSvg>(), "svg", true);
            if (message.Is(RunSchematicJobExportDxf.Descriptor))
                return Run(message.Unpack<RunSchematicJobExportDxf>(), "dxf", true);
            if (message.Is(RunSchematicJobExportPdf.Descriptor))
                return Run(message.Unpack<RunSchematicJobExportPdf>(), "pdf", false);
            if (message.Is(RunSchematicJobExportPs.Descriptor))
                return Run(message.Unpack<RunSchematicJobExportPs>(), "ps", true);
            if (message.Is(RunSchematicJobExportNetlist.Descriptor))
                return Run(message.Unpack<RunSchematicJobExportNetlist>(), "netlist", false);
            if (message.Is(RunSchematicJobExportBOM.Descriptor))
                return Run(message.Unpack<RunSchematicJobExportBOM>(), "bom", false);
            throw new InvalidOperationException("Unexpected fixture request: " + message.Descriptor.FullName);
        }

        private Task<byte[]> Run(IMessage request, string kind, bool directoryOutput)
        {
            JobPayloads.Add(request.ToByteArray());
            RunJobSettings settings = request switch
            {
                RunSchematicJobExportSvg svg => svg.JobSettings,
                RunSchematicJobExportDxf dxf => dxf.JobSettings,
                RunSchematicJobExportPdf pdf => pdf.JobSettings,
                RunSchematicJobExportPs ps => ps.JobSettings,
                RunSchematicJobExportNetlist netlist => netlist.JobSettings,
                RunSchematicJobExportBOM bom => bom.JobSettings,
                _ => throw new InvalidOperationException("Unsupported fixture export")
            };
            string outputPath = Path.GetFullPath(settings.OutputPath);
            string reportedPath = outputPath;
            if (directoryOutput)
            {
                Directory.CreateDirectory(outputPath);
                string file = Path.Combine(outputPath, kind + "." + (kind == "svg" ? "svg" : kind));
                File.WriteAllText(file, "fixture-" + kind);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "fixture-" + kind);
            }
            return Reply(new RunJobResponse { Status = JobStatus.JsSuccess, OutputPath = { reportedPath } });
        }

        private Task<byte[]> Reply(IMessage payload)
            => Task.FromResult(new ApiResponse
            {
                Header = new() { KicadToken = epoch },
                Status = new() { Status = (ApiStatusCode)1 },
                Message = Any.Pack(payload)
            }.ToByteArray());
    }
}
