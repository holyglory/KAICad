using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMcpReattachment(ProcessStartInfo start, string instanceId,
        DocumentSpecifier document, SchematicScreenDataSnapshot expected, SchematicSaveState dirty,
        CancellationToken token)
    {
        using Process process = Process.Start(start)!;
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(token);
        int nextId = 0;
        try
        {
            await Request("initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "native-reattachment-journey", version = "1" }
            });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var saved = await Call("kicad_instance_saved_sessions", new { });
            using var records = JsonDocument.Parse(saved.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual(instanceId, records.RootElement[0].GetProperty("instanceId").GetString());
            await Call("kicad_instance_reattach", new { instanceId });
            await VerifyInstanceCapabilityCatalogue();
            string documentJson = JsonFormatter.Default.Format(document);
            var state = await Call("kicad_schematic_save_state", new { instanceId, documentJson });
            Assert.AreEqual(dirty, SchematicJson.Parser.Parse<SchematicSaveState>(state.GetProperty("structuredContent").GetProperty("state").GetRawText()));
            var snapshot = await Call("kicad_schematic_data", new { instanceId, documentJson });
            Assert.AreEqual(expected, SchematicJson.Parser.Parse<SchematicScreenDataSnapshot>(snapshot.GetProperty("structuredContent").GetProperty("snapshot").GetRawText()),
                "Reconnecting must preserve unsaved in-memory objects and their revision, not reload disk contents.");
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.AreEqual(0, process.ExitCode, await diagnostics);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }

        // The reattached server reports exactly what is registered: its service tools equal tools/list,
        // its native features and requests equal a handshake read directly from KiCad, and that
        // handshake's handled requests equal the request types this process really dispatches with its
        // schematic editor open.
        async Task VerifyInstanceCapabilityCatalogue()
        {
            var listed = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = (await Request("tools/list", cursor is null ? new { } : (object)new { cursor })).GetProperty("result");
                listed.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.Clone()));
                cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (cursor is not null);
            string state = start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"]!;
            var record = JsonSerializer.Deserialize<InstanceRecord>(await File.ReadAllTextAsync(Path.Combine(state, instanceId + ".json"), token))!;
            var direct = new NativeClient(new NngTransport(), record.Endpoint, record.Epoch);
            var session = await direct.HandshakeAsync(token);
            string[] features = NativeFeatureContracts.Verify(session);

            var attached = JsonSerializer.Deserialize<InstanceView[]>((await Call("kicad_instances_list", new { }))
                .GetProperty("content")[0].GetProperty("text").GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.AreEqual(instanceId, attached.Single().InstanceId);
            var catalogue = (await Call("kicad_instance_capabilities", new { instanceId })).GetProperty("structuredContent");
            string[] serviceNames = CapabilityCatalogAssertions.VerifyServiceCatalogue(catalogue, listed);
            Assert.AreEqual(instanceId, catalogue.GetProperty("instanceId").GetString());
            Assert.AreEqual(session.Epoch, catalogue.GetProperty("epoch").GetString());
            CollectionAssert.AreEqual(features, catalogue.GetProperty("nativeFeatures").EnumerateArray().Select(n => n.GetString()!).ToArray(),
                "The MCP catalogue must carry the native feature contracts unchanged.");
            Assert.AreEqual(CapabilityCatalog.HandledRequestCoverage, catalogue.GetProperty("nativeRequestCoverage").GetString());
            var native = catalogue.GetProperty("nativeRequests").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(session.HandledRequests.ToArray(), native.Select(n => n.GetProperty("name").GetString()!).ToArray(),
                "The MCP catalogue must carry the native handled requests unchanged.");
            Assert.IsTrue(native.All(n => n.GetProperty("availability").GetString() == "handler-registered"));
            Assert.IsFalse(catalogue.TryGetProperty("nativeCapabilities", out _), "Catalogue v2 names features and requests separately.");
            var service = (await Call("kicad_service_capabilities", new { })).GetProperty("structuredContent");
            Assert.AreEqual(catalogue.GetProperty("serviceCapabilities").GetRawText(), service.GetProperty("serviceCapabilities").GetRawText());
            Assert.AreEqual(catalogue.GetProperty("limitations").GetRawText(), service.GetProperty("limitations").GetRawText());
            var inspected = (await Call("kicad_instance_inspect", new { instanceId })).GetProperty("structuredContent");
            CollectionAssert.AreEqual(features, inspected.GetProperty("nativeCapabilities").EnumerateArray()
                .Select(n => n.GetString()!).ToArray(), "Inspection keeps nativeCapabilities as the feature contracts.");
            Assert.AreEqual(CapabilityCatalog.HandledRequestCoverage, inspected.GetProperty("nativeRequestCoverage").GetString());
            CollectionAssert.AreEqual(session.HandledRequests.ToArray(), inspected.GetProperty("nativeRequests").EnumerateArray()
                .Select(n => n.GetProperty("name").GetString()!).ToArray());

            string artifacts = Path.Combine(FindRoot(), "automation", "artifacts", "native-session-current");
            string[] advertised = await NativeCapabilityProbe.VerifyHandshakeAsync(direct,
                Directory.Exists(artifacts) ? Path.Combine(artifacts, instanceId + "-editor-capabilities.json") : null, token);
            // With the schematic editor open, its handler and both automation controllers are dispatched.
            foreach (string type in new[] { GetAutomationSession.Descriptor.FullName, GetVersion.Descriptor.FullName,
                         ReadSchematicScreenData.Descriptor.FullName, CheckedSchematicBatch.Descriptor.FullName, CheckedSaveDocument.Descriptor.FullName })
                CollectionAssert.Contains(advertised, type);
            var documents = SchematicJson.Parser.Parse<GetOpenDocumentsResponse>((await Call("kicad_documents_list",
                new { instanceId, kind = "schematic" })).GetProperty("content")[0].GetProperty("text").GetString()!);
            CollectionAssert.Contains(documents.Documents.ToArray(), document);
            Console.WriteLine($"Capability catalogue {instanceId}: {serviceNames.Length} registered tools, {features.Length} native features, {advertised.Length} native request types.");
        }

        async Task<JsonElement> Call(string name, object arguments)
        {
            var response = await Request("tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
            return result;
        }

        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                Assert.IsNotNull(line, "Restarted MCP exited before responding.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    return response.RootElement.Clone();
            }
        }
    }
}
