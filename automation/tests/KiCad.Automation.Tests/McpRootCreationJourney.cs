using System.Diagnostics;
using System.Text.Json;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<OpenDocumentResponse> CreateRootThroughMcp(string endpoint, string instanceId,
        string path, string evidence, CancellationToken token, string? publishedExecutable = null,
        string toolName = "kicad_schematic_create")
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string state = Directory.CreateTempSubdirectory("kicad-mcp-create-").FullName;
        string log = Path.Combine(evidence, "mcp-create-" + Guid.NewGuid().ToString("N") + ".stderr.log");
        var start = new ProcessStartInfo(publishedExecutable ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (publishedExecutable is null)
            start.ArgumentList.Add(Path.Combine(FindRoot(), "automation", "src", "KiCad.Automation.Mcp", "bin",
                configuration, "net10.0", "kicad-mcp.dll"));
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
        using var process = Process.Start(start)!;
        var diagnostics = Capture(process.StandardError, log);
        int nextId = 0;
        try
        {
            await Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "native-root-creation-journey", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await Tool("kicad_instance_attach", new { endpoint, expectedInstanceId = instanceId });
            var result = await Tool(toolName, new { instanceId, path });
            var text = result.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
            return new OpenDocumentResponse { Document = SchematicJson.Parser.Parse<DocumentSpecifier>(text) };
        }
        finally
        {
            process.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exit.Token); }
            catch (OperationCanceledException)
            {
                // Only the fixture's MCP process is disposable; never kill the
                // native editor when its controlling client disconnects.
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                throw new AssertFailedException("MCP creation client did not stop after STDIO closed.");
            }
            finally
            {
                await diagnostics;
                Directory.Delete(state, true);
            }
        }

        async Task<JsonElement> Tool(string name, object arguments)
        {
            var result = (await Request("tools/call", new { name, arguments })).GetProperty("result");
            Assert.IsFalse(result.TryGetProperty("isError", out var failed) && failed.GetBoolean(),
                name + ": " + result.GetRawText());
            return result;
        }

        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                Assert.IsNotNull(line, "MCP exited before responding; inspect retained creation diagnostics.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                {
                    Assert.IsFalse(response.RootElement.TryGetProperty("error", out _), response.RootElement.GetRawText());
                    return response.RootElement.Clone();
                }
            }
        }
    }
}
