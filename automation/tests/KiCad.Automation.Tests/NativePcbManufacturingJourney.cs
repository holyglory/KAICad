using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    /// <summary>
    /// Creates a real manufacturing fixture after closing the board supplied by the caller,
    /// then proves the native drill and pick-and-place jobs through the public MCP tools.
    /// The fixture is disposable and the returned document remains open for the parent journey.
    /// </summary>
    private static async Task<DocumentSpecifier> VerifyNativePcbManufacturing(NativeClient client,
        DocumentSpecifier currentBoard, string evidence, string instanceId, CancellationToken token)
    {
        string boardPath = Path.GetFullPath(Path.Combine(currentBoard.Project.Path, currentBoard.BoardFilename));
        await SaveCheckedThroughMcp(client, currentBoard, evidence, token, verifyReconnect: false);
        var saved = await ObserveLifecycleState(client, currentBoard, token);

        string closeState = SchematicJson.Formatter.Format(saved);
        string closeStateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-manufacturing-close-").FullName;
        try
        {
            await using var closeMcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                closeStateDirectory, Path.Combine(evidence, instanceId + "-manufacturing-close.stderr.log"), token);
            var attached = await closeMcp.Tool("kicad_instance_attach",
                new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean(),
                attached.GetRawText());
            var closed = await closeMcp.Tool("kicad_document_close", new
            {
                instanceId,
                expectedStateJson = closeState,
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsFalse(closed.TryGetProperty("isError", out var closeError) && closeError.GetBoolean(),
                closed.GetRawText());
            var result = ParseLifecycleResult(closed);
            Assert.AreEqual(LifecycleOperationStatus.LosClosed, result.Status, closed.GetRawText());
            Assert.AreEqual(currentBoard, result.Document);
        }
        finally { Directory.Delete(closeStateDirectory, true); }

        string modelPath = Path.Combine(currentBoard.Project.Path, "TO-252-2.step");
        string repositoryModel = Path.Combine(RepositoryRoot(), "qa", "data", "pcbnew", "step_model_colors", "TO-252-2.step");
        File.Copy(repositoryModel, modelPath, overwrite: true);
        await File.WriteAllTextAsync(boardPath, ManufacturingFixture(modelPath), token);
        var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
            toolName: "kicad_pcb_open");
        var board = opened.Document;
        var before = await ObserveLifecycleState(client, board, token);

        string exportDirectory = Directory.CreateTempSubdirectory("kicad-pcb-manufacturing-export-").FullName;
        string mcpStateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-manufacturing-mcp-").FullName;
        string retained = Path.Combine(evidence, instanceId + "-manufacturing");
        Directory.CreateDirectory(retained);
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                mcpStateDirectory, Path.Combine(evidence, instanceId + "-manufacturing-mcp.stderr.log"), token);
            var attached = await mcp.Tool("kicad_instance_attach",
                new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean(),
                attached.GetRawText());

            string expectedStateJson = SchematicJson.Formatter.Format(before);
            string drillDirectory = Path.Combine(exportDirectory, "drill");
            var drill = new RunBoardJobExportDrill
            {
                JobSettings = new() { Document = board, OutputPath = drillDirectory },
                Format = DrillFormat.DfExcellon,
                Units = Units.UMm,
                Origin = DrillOrigin.DoAbsolute,
                ZerosFormat = DrillZerosFormat.DzfDecimal,
                Excellon = new() { CombinePthNpth = false, MinimalHeader = false }
            };
            var drillReply = await mcp.Tool("kicad_pcb_export_drill", new
            {
                instanceId,
                requestJson = BoardJson.Formatter.Format(drill),
                expectedStateJson
            });
            Assert.IsFalse(drillReply.TryGetProperty("isError", out var drillError) && drillError.GetBoolean(),
                drillReply.GetRawText());
            var drillData = drillReply.GetProperty("structuredContent");
            Assert.AreEqual(expectedStateJson, drillData.GetProperty("beforeState").GetString());
            Assert.AreEqual(expectedStateJson, drillData.GetProperty("afterState").GetString());
            var drillFiles = await RetainAndVerifyOutputs(drillData, retained, "drill", token);
            var pth = drillFiles.SingleOrDefault(file => Path.GetFileName(file).Contains("pth", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(file).Contains("npth", StringComparison.OrdinalIgnoreCase));
            var npth = drillFiles.SingleOrDefault(file => Path.GetFileName(file).Contains("npth", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(pth, "KiCad must emit a separate plated drill file when PTH/NPTH combination is disabled.");
            Assert.IsNotNull(npth, "KiCad must emit a separate non-plated drill file when PTH/NPTH combination is disabled.");
            string pthText = await File.ReadAllTextAsync(pth!, token);
            string npthText = await File.ReadAllTextAsync(npth!, token);
            AssertDrill(pthText, 0.9m, 40m, -40m, "J1 plated drill");
            AssertDrill(npthText, 1.1m, 50m, -40m, "NPTH non-plated drill");

            string positionDirectory = Path.Combine(exportDirectory, "position");
            var position = new RunBoardJobExportPosition
            {
                JobSettings = new() { Document = board, OutputPath = positionDirectory },
                SmdOnly = true, ExcludeFootprintsWithTh = true, ExcludeDnp = false,
                ExcludeFromBom = false, SingleFile = false, NakedFilename = false,
                Side = PositionSide.PsBoth, Units = Units.UMm, Format = PositionFormat.PfCsv
            };
            var positionReply = await mcp.Tool("kicad_pcb_export_position", new
            {
                instanceId,
                requestJson = BoardJson.Formatter.Format(position),
                expectedStateJson
            });
            Assert.IsFalse(positionReply.TryGetProperty("isError", out var positionError) && positionError.GetBoolean(),
                positionReply.GetRawText());
            var positionData = positionReply.GetProperty("structuredContent");
            Assert.AreEqual(expectedStateJson, positionData.GetProperty("beforeState").GetString());
            Assert.AreEqual(expectedStateJson, positionData.GetProperty("afterState").GetString());
            var positionFiles = await RetainAndVerifyOutputs(positionData, retained, "position", token);
            Assert.IsTrue(positionFiles.Count >= 2, "Both front and back position files must be emitted.");
            string positionText = string.Join("\n", await Task.WhenAll(positionFiles.Select(path => File.ReadAllTextAsync(path, token))));
            AssertPlacement(positionText, "R1", 30m, 30m, front: true);
            AssertPlacement(positionText, "R2", 60m, 60m, front: false);

            var ipc = new RunBoardJobExportIpc2581
            {
                JobSettings = new() { Document = board, OutputPath = Path.Combine(exportDirectory, "ipc2581.xml") },
                Units = Units.UMm, Version = (Ipc2581Version)1, Precision = 6
            };
            var ipcReply = await mcp.Tool("kicad_pcb_export_ipc2581", new
            { instanceId, requestJson = BoardJson.Formatter.Format(ipc), expectedStateJson });
            Assert.IsFalse(ipcReply.TryGetProperty("isError", out var ipcError) && ipcError.GetBoolean(), ipcReply.GetRawText());
            var ipcFiles = await RetainAndVerifyOutputs(ipcReply.GetProperty("structuredContent"), retained, "ipc2581", token);
            Assert.IsTrue(ipcFiles.Any(file => new[] { ".xml", ".zip" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)));

            var d356 = new RunBoardJobExportIpcD356
            { JobSettings = new() { Document = board, OutputPath = Path.Combine(exportDirectory, "ipc-d356") } };
            var d356Reply = await mcp.Tool("kicad_pcb_export_ipc_d356", new
            { instanceId, requestJson = BoardJson.Formatter.Format(d356), expectedStateJson });
            Assert.IsFalse(d356Reply.TryGetProperty("isError", out var d356Error) && d356Error.GetBoolean(), d356Reply.GetRawText());
            var d356Files = await RetainAndVerifyOutputs(d356Reply.GetProperty("structuredContent"), retained, "ipc-d356", token);
            Assert.IsTrue(d356Files.Any(file => new FileInfo(file).Length > 0));

            var odb = new RunBoardJobExportODB
            {
                JobSettings = new() { Document = board, OutputPath = Path.Combine(exportDirectory, "odb") },
                Units = Units.UMm, Compression = (OdbCompression)2
            };
            var odbReply = await mcp.Tool("kicad_pcb_export_odb", new
            { instanceId, requestJson = BoardJson.Formatter.Format(odb), expectedStateJson });
            Assert.IsFalse(odbReply.TryGetProperty("isError", out var odbError) && odbError.GetBoolean(), odbReply.GetRawText());
            var odbFiles = await RetainAndVerifyOutputs(odbReply.GetProperty("structuredContent"), retained, "odb", token);
            Assert.IsTrue(odbFiles.Any(file => Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Contains("odb", StringComparison.OrdinalIgnoreCase)));

            var export3d = new RunBoardJobExport3D
            {
                JobSettings = new() { Document = board, OutputPath = Path.Combine(exportDirectory, "board.step") },
                Format = (Board3DFormat)1,
                Overwrite = true,
                IncludeUnspecified = true,
                IncludeDnp = true,
                ExportBoardBody = true,
                ExportComponents = true,
                ExportPads = true,
                ExportTracksAndVias = true,
                ExportSilkscreen = true,
                ExportSoldermask = true
            };
            var export3dReply = await mcp.Tool("kicad_pcb_export_3d", new
            { instanceId, requestJson = BoardJson.Formatter.Format(export3d), expectedStateJson });
            Assert.IsFalse(export3dReply.TryGetProperty("isError", out var export3dError) && export3dError.GetBoolean(),
                export3dReply.GetRawText());
            var export3dFiles = await RetainAndVerifyOutputs(export3dReply.GetProperty("structuredContent"), retained, "3d", token);
            Assert.IsTrue(export3dFiles.Any(file => Path.GetExtension(file).Equals(".step", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(file).Equals(".stp", StringComparison.OrdinalIgnoreCase)), export3dReply.GetRawText());

            // The typed task-oriented export owns a new directory and keeps a
            // native receipt that can be replayed after an MCP reconnect.
            string typedOutputDirectory = Path.Combine(exportDirectory, "typed-step");
            string typedOperationId = Guid.NewGuid().ToString("D");
            var typedDocument = new
            {
                type = "DOCTYPE_PCB",
                project = new { name = board.Project.Name, path = board.Project.Path },
                boardFilename = board.BoardFilename
            };
            JsonElement typedExpectedState = JsonDocument.Parse(expectedStateJson).RootElement.Clone();
            object typedInput = new
            {
                operationId = typedOperationId,
                document = typedDocument,
                expectedState = typedExpectedState,
                outputDirectory = typedOutputDirectory,
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
                        precision = 6,
                        pageMode = "BJPM_ALL_LAYERS_ONE_PAGE"
                    }
                }
            };
            var typedReply = await mcp.Tool("kicad_pcb_export", new { instanceId, input = typedInput });
            Assert.IsFalse(typedReply.TryGetProperty("isError", out var typedError) && typedError.GetBoolean(), typedReply.GetRawText());
            var typedData = typedReply.GetProperty("structuredContent");
            Assert.AreEqual("PcbMfgExportCompleted", typedData.GetProperty("status").GetString(), typedReply.GetRawText());
            Assert.IsTrue(typedData.GetProperty("outputManifestComplete").GetBoolean(), typedReply.GetRawText());
            Assert.IsTrue(Directory.Exists(typedOutputDirectory), typedReply.GetRawText());
            Assert.IsTrue(typedData.GetProperty("artifacts").GetArrayLength() > 0, typedReply.GetRawText());
            string typedSvg = typedData.GetProperty("artifacts").EnumerateArray()
                .Select(artifact => artifact.GetProperty("path").GetString()!)
                .Single(path => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase));
            string typedSvgText = await File.ReadAllTextAsync(typedSvg, token);
            StringAssert.Contains(typedSvgText, "F.Cu", "The selected copper layer must reach the native SVG output.");
            StringAssert.Contains(typedSvgText, "F.Silkscreen", "The selected silk layer must reach the native SVG output.");
            StringAssert.Contains(typedSvgText, "R1", "Nested plot reference settings must reach the native SVG output.");

            var replayReply = await mcp.Tool("kicad_pcb_export", new { instanceId, input = typedInput });
            Assert.IsFalse(replayReply.TryGetProperty("isError", out var replayError) && replayError.GetBoolean(), replayReply.GetRawText());
            Assert.AreEqual(typedData.GetRawText(), replayReply.GetProperty("structuredContent").GetRawText(),
                "Replaying an exact manufacturing request must return its retained native receipt.");

            string reconnectStateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-manufacturing-reconnect-").FullName;
            try
            {
                await using var reconnectMcp = await StdioMcpFixture.StartAsync(
                    SyncHarnessProcessTests.ProductionStartInfo(), reconnectStateDirectory,
                    Path.Combine(evidence, instanceId + "-manufacturing-reconnect.stderr.log"), token);
                var reconnect = await reconnectMcp.Tool("kicad_instance_attach",
                    new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(reconnect.TryGetProperty("isError", out var reconnectError) && reconnectError.GetBoolean(),
                    reconnect.GetRawText());
                var receiptReply = await reconnectMcp.Tool("kicad_pcb_export_receipt", new
                {
                    instanceId, document = typedDocument, processEpoch = before.ProcessEpoch, operationId = typedOperationId
                });
                Assert.IsFalse(receiptReply.TryGetProperty("isError", out var receiptError) && receiptError.GetBoolean(), receiptReply.GetRawText());
                Assert.IsTrue(receiptReply.GetProperty("structuredContent").GetProperty("found").GetBoolean(), receiptReply.GetRawText());
            }
            finally { Directory.Delete(reconnectStateDirectory, true); }

            var destinationRefusal = await mcp.Tool("kicad_pcb_export", new
            {
                instanceId,
                input = new
                {
                    operationId = Guid.NewGuid().ToString("D"), document = typedDocument,
                    expectedState = typedExpectedState, outputDirectory = typedOutputDirectory,
                    job = new { exportSvg = new { plotSettings = new { layers = new[] { "BL_F_Cu" } } } }
                }
            });
            Assert.IsTrue(destinationRefusal.TryGetProperty("isError", out var destinationError) && destinationError.GetBoolean(),
                destinationRefusal.GetRawText());

            var malformedRender = await mcp.Tool("kicad_pcb_export", new
            {
                instanceId,
                input = new
                {
                    operationId = Guid.NewGuid().ToString("D"), document = typedDocument,
                    expectedState = typedExpectedState, outputDirectory = Path.Combine(exportDirectory, "bad-render"),
                    job = new { exportRender = new { width = 32, height = 240, format = "RF_PNG" } }
                }
            });
            Assert.IsTrue(malformedRender.TryGetProperty("isError", out var malformedRenderError)
                && malformedRenderError.GetBoolean(), malformedRender.GetRawText());
            Assert.AreEqual("invalid_render_dimensions",
                malformedRender.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
                malformedRender.GetRawText());

            var after = await ObserveLifecycleState(client, board, token);
            Assert.AreEqual(before, after, "Manufacturing jobs must leave the live board and lifecycle state unchanged.");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manufacturing.json"),
                JsonSerializer.Serialize(new
                {
                    document = board, lifecycle = SchematicJson.Formatter.Format(after),
                    drill = drillFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
                    position = positionFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
                    threeD = export3dFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            Directory.Delete(exportDirectory, true);
            Directory.Delete(mcpStateDirectory, true);
        }

        return board;
    }

    private static LifecycleOperationResult ParseLifecycleResult(JsonElement reply) =>
        SchematicJson.Parser.Parse<LifecycleOperationResult>(reply.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);

    private static async Task<IReadOnlyList<string>> RetainAndVerifyOutputs(JsonElement structured, string retained,
        string kind, CancellationToken token)
    {
        var outputs = structured.GetProperty("outputs").EnumerateArray().ToArray();
        Assert.IsGreaterThan(0, outputs.Length, $"{kind} export returned no output files.");
        var retainedPaths = new List<string>(outputs.Length);
        foreach (var output in outputs)
        {
            string source = output.GetProperty("path").GetString()!;
            Assert.IsTrue(File.Exists(source), source);
            byte[] bytes = await File.ReadAllBytesAsync(source, token);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.AreEqual(output.GetProperty("bytes").GetInt64(), bytes.LongLength, source);
            Assert.IsTrue(string.Equals(actualHash, output.GetProperty("sha256").GetString(), StringComparison.Ordinal), source);
            string target = Path.Combine(retained, kind + "-" + Path.GetFileName(source));
            await File.WriteAllBytesAsync(target, bytes, token);
            retainedPaths.Add(target);
        }
        return retainedPaths;
    }

    private static void AssertDrill(string text, decimal diameter, decimal x, decimal y, string because)
    {
        var diameterMatch = System.Text.RegularExpressions.Regex.Matches(text,
            @"(?im)^T\d+C(?<value>[+-]?\d+(?:\.\d+)?)")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture))
            .Any(value => ScaledNear(value, diameter));
        Assert.IsTrue(diameterMatch, because + " must contain the requested drill diameter.");
        var coordinateMatch = System.Text.RegularExpressions.Regex.Matches(text,
            @"(?im)^X(?<x>[+-]?\d+(?:\.\d+)?)Y(?<y>[+-]?\d+(?:\.\d+)?)")
            .Cast<System.Text.RegularExpressions.Match>()
            .Any(match => ScaledNear(decimal.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture), x)
                && ScaledNear(decimal.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), y));
        Assert.IsTrue(coordinateMatch, because + " must contain the requested hole coordinate.");
    }

    private static bool ScaledNear(decimal actual, decimal expected)
    {
        decimal scale = 1;
        for (int i = 0; i < 8; i++)
        {
            if (Math.Abs(actual / scale - expected) < 0.01m) return true;
            scale *= 10;
        }
        return false;
    }

    private static void AssertPlacement(string text, string reference, decimal x, decimal y, bool front)
    {
        string? row = text.Split('\n').FirstOrDefault(line => line.Split(',').Any(token =>
            token.Trim().Trim('"').Equals(reference, StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(row, $"Position CSV must contain {reference}.");
        var numbers = row!.Split(',').Select(token => token.Trim().Trim('"'))
            .Select(token => decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? (decimal?)value : null).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        Assert.IsTrue(numbers.Any(value => Math.Abs(Math.Abs(value) - Math.Abs(x)) < 0.01m), $"{reference} X coordinate missing.");
        Assert.IsTrue(numbers.Any(value => Math.Abs(Math.Abs(value) - Math.Abs(y)) < 0.01m), $"{reference} Y coordinate missing.");
        string lower = row.ToLowerInvariant();
        string[] sideWords = front ? ["top", "front", "f.cu"] : ["bottom", "back", "b.cu"];
        Assert.IsTrue(sideWords.Any(lower.Contains), $"{reference} side is missing from position CSV.");
    }

    private static string ManufacturingFixture(string modelPath)
    {
        string j1 = Guid.NewGuid().ToString("D"), j1Pad = Guid.NewGuid().ToString("D");
        string npth = Guid.NewGuid().ToString("D"), npthPad = Guid.NewGuid().ToString("D");
        string r1 = Guid.NewGuid().ToString("D"), r1a = Guid.NewGuid().ToString("D"), r1b = Guid.NewGuid().ToString("D");
        string r2 = Guid.NewGuid().ToString("D"), r2a = Guid.NewGuid().ToString("D"), r2b = Guid.NewGuid().ToString("D");
        string edge = Guid.NewGuid().ToString("D");
        return $"""
(kicad_pcb (version 20260101) (generator "pcbnew")
  (general (thickness 1.6))
  (paper "A4")
  (layers (0 "F.Cu" signal) (2 "B.Cu" signal) (5 "F.SilkS" user "F.Silkscreen")
    (7 "B.SilkS" user "B.Silkscreen") (9 "F.Paste" user) (11 "B.Paste" user)
    (13 "F.Mask" user) (15 "B.Mask" user) (25 "Edge.Cuts" user))
  (setup (pad_to_mask_clearance 0))
  (net 0 "")
  (gr_rect (start 0 0) (end 80 80) (stroke (width 0.05) (type default)) (fill none) (layer "Edge.Cuts") (uuid "{edge}"))
  (footprint "Connector:TestHeader" (layer "F.Cu") (at 40 40) (uuid "{j1}")
    (property "Reference" "J1" (at 0 -2 0) (layer "F.SilkS") (effects (font (size 1 1) (thickness 0.15))))
    (property "Value" "PWR_IN" (at 0 2 0) (layer "F.Fab") (effects (font (size 1 1) (thickness 0.15))))
    (attr through_hole)
    (pad "1" thru_hole circle (at 0 0) (size 1.8 1.8) (drill 0.9) (layers "*.Cu" "*.Mask") (uuid "{j1Pad}")))
  (footprint "MountingHole:NPTH" (layer "F.Cu") (at 50 40) (uuid "{npth}")
    (property "Reference" "H1" (at 0 -2 0) (layer "F.SilkS") (effects (font (size 1 1) (thickness 0.15))))
    (property "Value" "NPTH_1.1" (at 0 2 0) (layer "F.Fab") (effects (font (size 1 1) (thickness 0.15))))
    (attr through_hole)
    (pad "" np_thru_hole circle (at 0 0) (size 2.2 2.2) (drill 1.1) (layers "*.Cu" "*.Mask") (uuid "{npthPad}")))
  (footprint "Resistor_SMD:R_0603" (layer "F.Cu") (at 30 30) (uuid "{r1}")
    (property "Reference" "R1" (at 0 -1.3 0) (layer "F.SilkS") (effects (font (size 1 1) (thickness 0.15))))
    (property "Value" "10k" (at 0 1.3 0) (layer "F.Fab") (effects (font (size 1 1) (thickness 0.15))))
    (attr smd)
    (model "{modelPath}" (offset (xyz 0 0 0)) (scale (xyz 1 1 1)) (rotate (xyz 0 0 0)))
    (pad "1" smd roundrect (at -0.8 0) (size 0.8 0.9) (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.2) (uuid "{r1a}"))
    (pad "2" smd roundrect (at 0.8 0) (size 0.8 0.9) (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.2) (uuid "{r1b}")))
  (footprint "Resistor_SMD:R_0603" (layer "B.Cu") (at 60 60) (uuid "{r2}")
    (property "Reference" "R2" (at 0 -1.3 0) (layer "B.SilkS") (effects (font (size 1 1) (thickness 0.15))))
    (property "Value" "22k" (at 0 1.3 0) (layer "B.Fab") (effects (font (size 1 1) (thickness 0.15))))
    (attr smd)
    (pad "1" smd roundrect (at -0.8 0) (size 0.8 0.9) (layers "B.Cu" "B.Paste" "B.Mask") (roundrect_rratio 0.2) (uuid "{r2a}"))
    (pad "2" smd roundrect (at 0.8 0) (size 0.8 0.9) (layers "B.Cu" "B.Paste" "B.Mask") (roundrect_rratio 0.2) (uuid "{r2b}")))
)
""";
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The KiCad repository root could not be located for the 3D fixture.");
    }
}
