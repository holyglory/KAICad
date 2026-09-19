using System.Text.Json;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Client;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMcpGeometry(NativeClient native, string instanceId,
        MeasureSchematicPlacement request, SchematicPlacementGeometry expected, string evidence, CancellationToken token)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string state = Directory.CreateTempSubdirectory("kicad-mcp-geometry-").FullName;
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin",
                    configuration, "net10.0", "kicad-mcp.dll")],
                EnvironmentVariables = new Dictionary<string, string?> { ["KICAD_AUTOMATION_STATE_DIRECTORY"] = state }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: token);
            var attached = await client.CallToolAsync("kicad_instance_attach", new Dictionary<string, object?>
                { ["endpoint"] = native.Endpoint, ["expectedInstanceId"] = instanceId }, cancellationToken: token);
            Assert.IsFalse(attached.IsError == true);
            async Task<ModelContextProtocol.Protocol.CallToolResult> Read(MeasureSchematicPlacement query) =>
                await client.CallToolAsync("kicad_schematic_measure_placement", new Dictionary<string, object?>
                    { ["instanceId"] = instanceId, ["requestJson"] = SchematicJson.Formatter.Format(query) }, cancellationToken: token);
            var observed = await Read(request);
            Assert.IsFalse(observed.IsError == true);
            Assert.AreEqual(expected, SchematicJson.Parser.Parse<SchematicPlacementGeometry>(
                observed.StructuredContent!.Value.GetProperty("geometry").GetRawText()));
            var stale = request.Clone(); stale.ExpectedRevision.Sequence++;
            Assert.IsTrue((await Read(stale)).IsError == true);
            var recovered = await Read(request);
            Assert.IsFalse(recovered.IsError == true);
            Assert.AreEqual(observed.StructuredContent.Value.GetRawText(), recovered.StructuredContent!.Value.GetRawText());
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-mcp-geometry-"
                + request.Document.SheetPath.Path[^1].Value + ".json"), observed.StructuredContent.Value.GetRawText(), token);
        }
        finally { Directory.Delete(state, true); }
    }
}
