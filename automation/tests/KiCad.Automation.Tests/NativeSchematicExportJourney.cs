using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        CollectionAssert.Contains(names.ToArray(), "kicad_schematic_export_svg");
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        DocumentLifecycleState clean = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(clean.NativeContentDirty, "The export baseline must be a saved native schematic.");
        string cleanJson = SchematicJson.Formatter.Format(clean);
        byte[] sourceBefore = await File.ReadAllBytesAsync(schematicPath, token);
        string outputRoot = Path.Combine(evidence, "svg-output");

        RunSchematicJobExportSvg Request(string outputPath) => new()
        {
            JobSettings = new() { Document = document.Clone(), OutputPath = outputPath },
            PlotSettings = new() { PlotAll = true, PlotDrawingSheet = false }
        };

        JsonElement completed = await mcp.Tool("kicad_schematic_export_svg", new
        {
            instanceId,
            requestJson = SchematicJson.Formatter.Format(Request(outputRoot)),
            expectedStateJson = cleanJson
        });
        RequireToolSuccess(completed);
        JsonElement structured = completed.GetProperty("structuredContent");
        Assert.AreEqual(instanceId, structured.GetProperty("instanceId").GetString());
        Assert.IsTrue(structured.GetProperty("nativeJob").GetBoolean(), completed.GetRawText());
        Assert.IsFalse(structured.GetProperty("boardMutation").GetBoolean(), completed.GetRawText());
        Assert.IsFalse(structured.GetProperty("schematicMutation").GetBoolean(), completed.GetRawText());
        Assert.AreEqual(cleanJson, structured.GetProperty("beforeState").GetString());
        Assert.AreEqual(cleanJson, structured.GetProperty("afterState").GetString());

        JsonElement[] outputs = structured.GetProperty("outputs").EnumerateArray().ToArray();
        Assert.IsGreaterThan(0, outputs.Length, completed.GetRawText());
        bool markerFound = false;
        string outputRootFull = Path.GetFullPath(outputRoot) + Path.DirectorySeparatorChar;
        foreach (JsonElement output in outputs)
        {
            string path = output.GetProperty("path").GetString()!;
            Assert.IsTrue(path.StartsWith(outputRootFull, StringComparison.Ordinal), completed.GetRawText());
            Assert.AreEqual(".svg", Path.GetExtension(path), true, completed.GetRawText());
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            Assert.AreEqual(bytes.LongLength, output.GetProperty("bytes").GetInt64(), completed.GetRawText());
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                output.GetProperty("sha256").GetString(), completed.GetRawText());
            string svg = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains(svg, "<svg", completed.GetRawText());
            markerFound |= svg.Contains(marker, StringComparison.Ordinal);
        }
        Assert.IsTrue(markerFound, "The native SVG must contain the unique text from this schematic fixture.");
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
            requestJson = SchematicJson.Formatter.Format(Request(staleOutput)),
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
            requestJson = SchematicJson.Formatter.Format(Request(dirtyOutput)),
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
