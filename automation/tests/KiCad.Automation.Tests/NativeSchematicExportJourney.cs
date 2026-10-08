using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Jobs;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeSchematicExport(NativeClient client, DocumentSpecifier document,
        string schematicPath, string marker, string evidence, string instanceId, CancellationToken token)
    {
        string statePath = Path.Combine(evidence, instanceId + "-export-mcp-state");
        string stderr = Path.Combine(evidence, instanceId + "-export-mcp.stderr.log");
        await using var mcp = await StdioMcpFixture.StartAsync(
            SyncHarnessProcessTests.ProductionStartInfo(), statePath, stderr, token);

        var listed = await mcp.ListTools(null);
        var names = listed.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string tool in new[] { "svg", "dxf", "pdf", "ps", "netlist", "bom" })
            CollectionAssert.Contains(names.ToArray(), "kicad_schematic_export_" + tool);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        DocumentLifecycleState clean = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(clean.NativeContentDirty, "The export baseline must be a saved native schematic.");
        string cleanJson = SchematicJson.Formatter.Format(clean);
        byte[] sourceBefore = await File.ReadAllBytesAsync(schematicPath, token);
        string instanceOutputRoot = Path.Combine(evidence, instanceId, "export-output");
        string svgOperationId = Guid.NewGuid().ToString("D");

        SchematicPlotSettings PlotSettings() => new() { PlotAll = true, PlotDrawingSheet = false };
        RunJobSettings JobSettings(string outputPath) => new()
        {
            Document = document.Clone(), OutputPath = outputPath
        };
        RunSchematicJobExportSvg SvgRequest(string outputPath) => new()
        {
            JobSettings = JobSettings(outputPath), PlotSettings = PlotSettings()
        };
        RunSchematicJobExportDxf DxfRequest(string outputPath) => new()
        {
            JobSettings = JobSettings(outputPath), PlotSettings = PlotSettings()
        };
        RunSchematicJobExportPdf PdfRequest(string outputPath) => new()
        {
            JobSettings = JobSettings(outputPath), PlotSettings = PlotSettings(), IncludeMetadata = true
        };
        RunSchematicJobExportPs PsRequest(string outputPath) => new()
        {
            JobSettings = JobSettings(outputPath), PlotSettings = PlotSettings()
        };
        RunSchematicJobExportNetlist NetlistRequest(string outputPath) => new()
        {
            JobSettings = JobSettings(outputPath), Format = SchematicNetlistFormat.SnfKicadXml
        };
        RunSchematicJobExportBOM BomRequest(string outputPath) => new()
        {
            JobSettings = JobSettings(outputPath), Format = new() { PresetName = "CSV" },
            Fields = new() { PresetName = "Default Editing" }
        };

        async Task<JsonElement> Export(string kind, IMessage request, string requestedPath,
            Func<string, byte[], string> contentCheck, string? operationId = null)
        {
            JsonElement reply = await mcp.Tool("kicad_schematic_export_" + kind, new
            {
                instanceId,
                requestJson = SchematicJson.Formatter.Format(request),
                expectedStateJson = cleanJson, operationId
            });
            RequireToolSuccess(reply);
            JsonElement structured = reply.GetProperty("structuredContent");
            Assert.AreEqual(instanceId, structured.GetProperty("instanceId").GetString(), reply.GetRawText());
            Assert.IsTrue(structured.GetProperty("nativeJob").GetBoolean(), reply.GetRawText());
            Assert.IsFalse(structured.GetProperty("boardMutation").GetBoolean(), reply.GetRawText());
            Assert.IsFalse(structured.GetProperty("schematicMutation").GetBoolean(), reply.GetRawText());
            Assert.AreEqual(cleanJson, structured.GetProperty("beforeState").GetString(), reply.GetRawText());
            Assert.AreEqual(cleanJson, structured.GetProperty("afterState").GetString(), reply.GetRawText());

            JsonElement[] outputs = structured.GetProperty("outputs").EnumerateArray().ToArray();
            Assert.IsGreaterThan(0, outputs.Length, reply.GetRawText());
            string requestedFull = Path.GetFullPath(requestedPath);
            bool requestedDirectory = string.IsNullOrEmpty(Path.GetExtension(requestedFull));
            string outputRootFull = requestedFull + Path.DirectorySeparatorChar;
            foreach (JsonElement output in outputs)
            {
                string path = output.GetProperty("path").GetString()!;
                Assert.IsTrue(Path.IsPathFullyQualified(path), reply.GetRawText());
                Assert.IsTrue(requestedDirectory
                    ? path.StartsWith(outputRootFull, StringComparison.Ordinal)
                    : string.Equals(path, requestedFull, StringComparison.Ordinal), reply.GetRawText());
                byte[] bytes = await File.ReadAllBytesAsync(path, token);
                Assert.IsGreaterThan(0L, bytes.LongLength, reply.GetRawText());
                Assert.AreEqual(bytes.LongLength, output.GetProperty("bytes").GetInt64(), reply.GetRawText());
                Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    output.GetProperty("sha256").GetString(), reply.GetRawText());
                Assert.AreEqual(64, output.GetProperty("sha256").GetString()!.Length, reply.GetRawText());
                string expectedExtension = kind switch
                {
                    "svg" => ".svg", "dxf" => ".dxf", "pdf" => ".pdf", "ps" => ".ps",
                    "netlist" => ".xml", "bom" => ".csv", _ => ""
                };
                Assert.AreEqual(expectedExtension, Path.GetExtension(path), true, reply.GetRawText());
                string detail = contentCheck(path, bytes);
                Assert.IsTrue(detail.Length == 0, kind + " content check failed: " + detail + "\n" + reply.GetRawText());
            }
            return structured;
        }

        string SvgDirectory = Path.Combine(instanceOutputRoot, "svg");
        string DxfDirectory = Path.Combine(instanceOutputRoot, "dxf");
        string PdfFile = Path.Combine(instanceOutputRoot, "pdf", "fixture.pdf");
        string PsDirectory = Path.Combine(instanceOutputRoot, "ps");
        string NetlistFile = Path.Combine(instanceOutputRoot, "netlist", "fixture.xml");
        string BomFile = Path.Combine(instanceOutputRoot, "bom", "fixture.csv");
        JsonElement svgReply = await Export("svg", SvgRequest(SvgDirectory), SvgDirectory, (_, bytes) =>
        {
            string value = Encoding.UTF8.GetString(bytes);
            return value.Contains("<svg", StringComparison.Ordinal)
                ? "" : "SVG signature missing";
        }, svgOperationId);
        Assert.IsTrue(svgReply.GetProperty("outputs").EnumerateArray()
            .Select(output => File.ReadAllText(output.GetProperty("path").GetString()!))
            .Any(value => value.Contains(marker, StringComparison.Ordinal)),
            "The native SVG must contain the unique text from this schematic fixture.");
        await Export("dxf", DxfRequest(DxfDirectory), DxfDirectory, (_, bytes) =>
        {
            string value = Encoding.UTF8.GetString(bytes);
            return value.Contains("SECTION", StringComparison.Ordinal) && value.Contains("EOF", StringComparison.Ordinal)
                ? "" : "DXF section or EOF marker missing";
        });
        await Export("pdf", PdfRequest(PdfFile), PdfFile, (_, bytes) =>
        {
            string value = Encoding.UTF8.GetString(bytes);
            return value.StartsWith("%PDF-", StringComparison.Ordinal) && value.Contains("KiCad PDF", StringComparison.Ordinal)
                ? "" : "PDF signature or KiCad producer missing";
        });
        await Export("ps", PsRequest(PsDirectory), PsDirectory, (_, bytes) =>
        {
            string value = Encoding.UTF8.GetString(bytes);
            return value.StartsWith("%!PS-Adobe", StringComparison.Ordinal) && value.Contains("Eeschema-PS", StringComparison.Ordinal)
                ? "" : "PostScript signature or KiCad creator missing";
        });
        await Export("netlist", NetlistRequest(NetlistFile), NetlistFile, (_, bytes) =>
        {
            string value = Encoding.UTF8.GetString(bytes);
            return value.Contains("<export", StringComparison.Ordinal) && value.Contains("TP1", StringComparison.Ordinal)
                && value.Contains("TP2", StringComparison.Ordinal) && value.Contains("SIGNAL", StringComparison.Ordinal)
                ? "" : "netlist XML is missing the fixture identities";
        });
        await Export("bom", BomRequest(BomFile), BomFile, (_, bytes) =>
        {
            string value = Encoding.UTF8.GetString(bytes);
            return value.Contains("TP1", StringComparison.Ordinal) && value.Contains("TP2", StringComparison.Ordinal)
                && value.Contains("Probe", StringComparison.Ordinal)
                ? "" : "BOM CSV is missing TP1/TP2 or Probe";
        });

        JsonElement svgReplay = await Export("svg", SvgRequest(SvgDirectory), SvgDirectory, (_, bytes) =>
            Encoding.UTF8.GetString(bytes).Contains("<svg", StringComparison.Ordinal) ? "" : "SVG signature missing",
            svgOperationId);
        RequireToolSuccess(svgReplay);
        JsonElement receipt = await mcp.Tool("kicad_schematic_export_receipt", new
        { instanceId, operationId = svgOperationId, processEpoch = clean.ProcessEpoch });
        Assert.IsFalse(receipt.TryGetProperty("isError", out var receiptError) && receiptError.GetBoolean(), receipt.GetRawText());
        Assert.IsTrue(receipt.GetProperty("structuredContent").GetProperty("found").GetBoolean(), receipt.GetRawText());

        Assert.AreEqual(clean, await ObserveLifecycleState(client, document, token),
            "Export must not change the live native lifecycle state.");
        CollectionAssert.AreEqual(sourceBefore, await File.ReadAllBytesAsync(schematicPath, token),
            "Export must not rewrite the schematic source.");

        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
        {
            Document = document.Clone(), TitleBlock = new TitleBlockInfo { Title = marker }
        }, token);
        DocumentLifecycleState dirty = await ObserveLifecycleState(client, document, token);
        Assert.IsTrue(dirty.NativeContentDirty, "The native title-block edit must create a dirty checkpoint.");

        string staleOutput = Path.Combine(evidence, "svg-stale-output");
        JsonElement stale = await mcp.Tool("kicad_schematic_export_svg", new
        {
            instanceId,
            requestJson = SchematicJson.Formatter.Format(SvgRequest(staleOutput)),
            expectedStateJson = cleanJson
        });
        Assert.IsTrue(stale.GetProperty("isError").GetBoolean(), stale.GetRawText());
        Assert.AreEqual("schematic_state_changed", stale.GetProperty("structuredContent")
            .GetProperty("errorCode").GetString(), stale.GetRawText());
        Assert.IsFalse(Directory.Exists(staleOutput), "A stale export must not create output.");
        Assert.AreEqual(dirty, await ObserveLifecycleState(client, document, token));

        string dirtyOutput = Path.Combine(evidence, "svg-dirty-output");
        JsonElement dirtyReply = await mcp.Tool("kicad_schematic_export_svg", new
        {
            instanceId,
            requestJson = SchematicJson.Formatter.Format(SvgRequest(dirtyOutput)),
            expectedStateJson = SchematicJson.Formatter.Format(dirty)
        });
        Assert.IsTrue(dirtyReply.GetProperty("isError").GetBoolean(), dirtyReply.GetRawText());
        Assert.AreEqual("schematic_export_dirty", dirtyReply.GetProperty("structuredContent")
            .GetProperty("errorCode").GetString(), dirtyReply.GetRawText());
        Assert.IsFalse(Directory.Exists(dirtyOutput), "A dirty export must not create output.");
        Assert.AreEqual(dirty, await ObserveLifecycleState(client, document, token));

        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document.Clone() }, token);
        DocumentLifecycleState restored = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(restored.NativeContentDirty);
        CollectionAssert.AreEqual(sourceBefore, await File.ReadAllBytesAsync(schematicPath, token));
    }
}
