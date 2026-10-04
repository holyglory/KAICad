using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Compiled STDIO coverage proves registration and the typed MCP schema. Native
// create/read/update/delete proof remains a separate open outcome until the
// checked native foundation and routing/DRC journey are integrated
// (p079fb6adb164b14c under p74ccef564f23a805).
[TestClass]
public sealed class PcbKeepoutMcpProcessTests
{
    [TestMethod]
    public async Task InitializeDiscoverAndCallOverStdio()
    {
        string root = FindAutomationRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.Combine(root, "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll");
        string state = Directory.CreateTempSubdirectory("kicad-keepout-mcp-test-").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(executable);
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
        using Process process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            JsonElement initialized = await Request(1, "initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "compiled-keepout-acceptance-fixture", version = "1" }
            });
            Assert.IsTrue(initialized.GetProperty("result").TryGetProperty("serverInfo", out _));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            JsonElement listed = await Request(2, "tools/list", new { });
            var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            foreach (string name in new[]
            {
                "kicad_pcb_keepout_read", "kicad_pcb_keepout_create",
                "kicad_pcb_keepout_update", "kicad_pcb_keepout_delete"
            })
                Assert.IsTrue(tools.Any(tool => tool.GetProperty("name").GetString() == name), name);

            JsonElement keepoutSchema = tools.Single(tool => tool.GetProperty("name").GetString() == "kicad_pcb_keepout_create")
                .GetProperty("inputSchema").GetProperty("properties").GetProperty("keepout");
            Assert.AreEqual("object", keepoutSchema.GetProperty("type").GetString());
            var properties = keepoutSchema.GetProperty("properties");
            foreach (string property in new[]
            {
                "id", "layers", "polygons", "name", "keepoutCopper", "keepoutVias",
                "keepoutTracks", "keepoutPads", "keepoutFootprints", "borderStyle", "borderPitchNm"
            })
                Assert.IsTrue(properties.TryGetProperty(property, out _), property);
            Assert.AreEqual("array", properties.GetProperty("layers").GetProperty("type").GetString());
            Assert.AreEqual("array", properties.GetProperty("polygons").GetProperty("type").GetString());

            string unknownInstance = Guid.NewGuid().ToString("D");
            string operationId = Guid.NewGuid().ToString("D");
            var keepout = new
            {
                id = Guid.NewGuid().ToString("D"), layers = new[] { "F.Cu", "F.Mask" },
                polygons = new[] { new
                {
                    outline = new[] { new { xNm = 0L, yNm = 0L }, new { xNm = 20_000L, yNm = 0L }, new { xNm = 0L, yNm = 20_000L } },
                    holes = Array.Empty<object>()
                } },
                name = "", keepoutCopper = true, keepoutVias = true, keepoutTracks = true,
                keepoutPads = false, keepoutFootprints = false, borderStyle = "ZBS_SOLID", borderPitchNm = 0L
            };
            foreach (var (requestId, name, arguments) in new (int, string, object)[]
            {
                (10, "kicad_pcb_keepout_read", new { instanceId = unknownInstance, documentJson = "{}", keepoutId = unknownInstance, expectedStateJson = "{}" }),
                (11, "kicad_pcb_keepout_create", new { instanceId = unknownInstance, documentJson = "{}", keepout, expectedStateJson = "{}", operationId }),
                (12, "kicad_pcb_keepout_update", new { instanceId = unknownInstance, documentJson = "{}", keepout, expectedStateJson = "{}", operationId }),
                (13, "kicad_pcb_keepout_delete", new { instanceId = unknownInstance, documentJson = "{}", keepoutId = unknownInstance, expectedStateJson = "{}", operationId })
            })
            {
                JsonElement refused = await Request(requestId, "tools/call", new { name, arguments });
                Assert.IsTrue(refused.GetProperty("result").GetProperty("isError").GetBoolean(), refused.GetRawText());
            }

            // Keep literal call sites bound to each advertised tool for capability evidence.
            await Request(14, "tools/call", new { name = "kicad_pcb_keepout_read", arguments = new { instanceId = unknownInstance, documentJson = "{}", keepoutId = unknownInstance, expectedStateJson = "{}" } });
            await Request(15, "tools/call", new { name = "kicad_pcb_keepout_create", arguments = new { instanceId = unknownInstance, documentJson = "{}", keepout, expectedStateJson = "{}", operationId } });
            await Request(16, "tools/call", new { name = "kicad_pcb_keepout_update", arguments = new { instanceId = unknownInstance, documentJson = "{}", keepout, expectedStateJson = "{}", operationId } });
            await Request(17, "tools/call", new { name = "kicad_pcb_keepout_delete", arguments = new { instanceId = unknownInstance, documentJson = "{}", keepoutId = unknownInstance, expectedStateJson = "{}", operationId } });
        }
        finally
        {
            process.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exit.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                throw new AssertFailedException("Compiled keepout MCP process did not stop after STDIO closed.");
            }
            await diagnostics;
            Directory.Delete(state, true);
        }

        async Task<JsonElement> Request(int id, string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.IsNotNull(line, "MCP exited before responding; inspect retained diagnostics.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                {
                    Assert.IsFalse(response.RootElement.TryGetProperty("error", out _), response.RootElement.GetRawText());
                    return response.RootElement.Clone();
                }
            }
        }
    }

    private static string FindAutomationRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "KiCad.Automation.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run this source test from the automation checkout.");
    }
}
