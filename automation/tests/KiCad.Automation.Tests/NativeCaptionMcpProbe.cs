using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>Actual packaged STDIO service held across the native caption journey.</summary>
internal sealed class NativeCaptionMcpProbe(string state, string evidence, CancellationToken token,
    Func<string, string, Task<IMcpToolClient>>? launch = null) : IAsyncDisposable
{
    private IMcpToolClient? mcp;
    private readonly Dictionary<string, Design> designs = new();
    private int starts;
    public int ReconnectedDesigns { get; private set; }
    public bool ServerRestartVerified { get; private set; }

    public async Task StartAsync(string executable)
    {
        Assert.IsNull(mcp, "Dispose the current MCP client before replacing it.");
        Assert.IsTrue(File.Exists(executable));
        string name = "caption-mcp-" + starts++;
        IMcpToolClient started;
        if (launch is not null) started = await launch(executable, name);
        else
        {
            var start = new ProcessStartInfo(executable);
            start.Environment.Remove("KICAD_AUTOMATION_NNG_LIBRARY");
            started = await StdioMcpFixture.StartAsync(start, state, Path.Combine(evidence, name + ".stderr.log"), token);
        }
        try { await RequireCaptionToolsAsync(started, token); mcp = started; }
        catch (Exception primary)
        {
            try { await started.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("MCP capability validation and cleanup both failed.", primary, cleanup); }
            throw;
        }
    }

    internal static async Task RequireCaptionToolsAsync(IMcpToolClient client, CancellationToken token)
    {
        string[] required = ["kicad_instance_attach", "kicad_instance_reattach",
            "kicad_schematic_observe", "kicad_instance_reconnect_after_update"];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            token.ThrowIfCancellationRequested();
            JsonElement page = await client.ListTools(cursor);
            token.ThrowIfCancellationRequested();
            Assert.IsTrue(page.ValueKind == JsonValueKind.Object && page.TryGetProperty("tools", out _), "MCP tools/list must return a tool catalog.");
            JsonElement tools = page.GetProperty("tools");
            Assert.AreEqual(JsonValueKind.Array, tools.ValueKind, "MCP tools/list tools must be an array.");
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                Assert.IsTrue(tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out _), "MCP tool has no name.");
                JsonElement value = tool.GetProperty("name");
                Assert.AreEqual(JsonValueKind.String, value.ValueKind, "MCP tool name must be a string.");
                string? toolName = value.GetString();
                Assert.IsFalse(string.IsNullOrWhiteSpace(toolName), "MCP tool name must not be empty.");
                Assert.IsTrue(names.Add(toolName!), "Duplicate MCP tool name: " + toolName);
            }
            cursor = null;
            if (page.TryGetProperty("nextCursor", out var next) && next.ValueKind != JsonValueKind.Null)
            {
                Assert.AreEqual(JsonValueKind.String, next.ValueKind, "MCP tools/list cursor must be a string.");
                cursor = next.GetString();
                Assert.IsFalse(string.IsNullOrEmpty(cursor), "MCP tools/list cursor must not be empty.");
                Assert.IsTrue(cursors.Add(cursor!), "MCP tools/list repeated a pagination cursor.");
            }
        } while (cursor is not null);
        string[] missing = required.Where(name => !names.Contains(name)).ToArray();
        Assert.IsEmpty(missing, "Update-test baseline is incompatible; missing MCP tools: " + string.Join(", ", missing));
    }

    public async Task AttachDesignAsync(string id, string name, NativeClient native, DocumentSpecifier document)
    {
        Success(await mcp!.Tool("kicad_instance_attach", new { endpoint = native.Endpoint, expectedInstanceId = id }));
        // The preservation marker needs a revision, not the newest full snapshot
        // schema. The baseline package may legitimately predate that schema.
        var before = await native.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        string markerId = Guid.NewGuid().ToString("D"), markerText = "Public update " + name + " note " + Guid.NewGuid().ToString("N");
        var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = before.Revision,
            DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Public update preservation fixture" };
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicText
        {
            Id = new() { Value = markerId }, Text = new() { Text_ = markerText,
                Position = new() { XNm = 100000000, YNm = 80000000 },
                Attributes = new() { Size = new() { XNm = 2000000, YNm = 2000000 } } }
        }) });
        await native.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        designs.Add(id, new(name, native.Epoch!, document, markerId, markerText, before.Revision.Epoch));
        await ObserveAsync(id, name + "-before-update", originalDocumentEpoch: true);
    }

    public async Task ObserveAsync(string id, string phase, bool originalDocumentEpoch)
    {
        Design design = designs[id]; JsonElement result;
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
        ready.CancelAfter(TimeSpan.FromSeconds(60));
        int delay = 50;
        while (true)
        {
            result = await mcp!.Tool("kicad_schematic_observe", new { instanceId = id, documentJson = SchematicJson.Formatter.Format(design.Document) }).WaitAsync(ready.Token);
            if (!IsError(result)) break;
            if (!RenderPending(result)) { Success(result); break; }
            await Task.Delay(delay, ready.Token); delay = Math.Min(delay * 2, 500);
        }
        Success(result);
        var content = result.GetProperty("structuredContent");
        Assert.AreEqual(id, content.GetProperty("instanceId").GetString());
        var observation = SchematicJson.Parser.Parse<SchematicObservation>(content.GetProperty("observation").GetRawText());
        Assert.AreEqual(design.Document, observation.Preview.Document);
        Assert.AreEqual(observation.Snapshot.Revision, observation.Preview.Revision);
        var marker = observation.Snapshot.Data.Items.Where(x => x.Is(SchematicText.Descriptor)).Select(x => x.Unpack<SchematicText>())
            .Single(x => x.Id.Value == design.MarkerId);
        Assert.AreEqual(design.MarkerText, marker.Text.Text_);
        if (originalDocumentEpoch) Assert.AreEqual(design.OriginalDocumentEpoch, observation.Snapshot.Revision.Epoch);
        else Assert.AreNotEqual(design.OriginalDocumentEpoch, observation.Snapshot.Revision.Epoch);
        byte[] image = Convert.FromBase64String(result.GetProperty("content").EnumerateArray()
            .Single(x => x.GetProperty("type").GetString() == "image").GetProperty("data").GetString()!);
        Assert.IsTrue(image.Length > 24 && image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        await File.WriteAllBytesAsync(Path.Combine(evidence, phase + "-mcp.png"), image, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, phase + "-mcp.json"), content.GetRawText(), token);
    }

    public async Task ReconnectAsync(string id, string installation, string operation, string expectedEpoch, string? restartExecutable = null)
    {
        Design design = designs[id];
        Assert.IsTrue(Guid.TryParseExact(operation, "D", out _));
        var arguments = new { instanceId = id, installationRoot = installation, operationId = operation, expectedOldEpoch = design.OriginalEpoch };
        var result = Success(await mcp!.Tool("kicad_instance_reconnect_after_update", arguments));
        var adopted = result.GetProperty("structuredContent");
        Assert.AreEqual(id, adopted.GetProperty("instanceId").GetString());
        Assert.AreEqual(expectedEpoch, adopted.GetProperty("epoch").GetString());
        Assert.IsFalse(adopted.GetProperty("reused").GetBoolean());
        Assert.IsTrue(adopted.GetProperty("freshSnapshotRequired").GetBoolean());
        await ObserveAsync(id, design.Name + "-after-update", originalDocumentEpoch: false);
        var retry = Success(await mcp.Tool("kicad_instance_reconnect_after_update", arguments));
        Assert.IsTrue(retry.GetProperty("structuredContent").GetProperty("reused").GetBoolean());
        ReconnectedDesigns++;
        foreach (var (otherId, other) in designs.Where(x => x.Key != id && !x.Value.Reconnected))
            await ObserveAsync(otherId, other.Name + "-neighbor-preserved", originalDocumentEpoch: true);
        design.Reconnected = true;

        if (restartExecutable is not null)
        {
            await mcp.DisposeAsync(); mcp = null;
            await StartAsync(restartExecutable);
            var restored = Success(await mcp!.Tool("kicad_instance_reconnect_after_update", arguments));
            Assert.IsTrue(restored.GetProperty("structuredContent").GetProperty("reused").GetBoolean());
            await ObserveAsync(id, design.Name + "-after-mcp-restart", originalDocumentEpoch: false);
            foreach (var (otherId, other) in designs.Where(x => x.Key != id))
            {
                Success(await mcp.Tool("kicad_instance_reattach", new { instanceId = otherId }));
                await ObserveAsync(otherId, other.Name + "-after-mcp-restart", originalDocumentEpoch: !other.Reconnected);
            }
            ServerRestartVerified = true;
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "caption-mcp-reconnection.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, actualPackagedMcp = true, actualPublicCaptionJourney = true, reconnectedDesigns = ReconnectedDesigns,
            serverRestartVerified = ServerRestartVerified, stableObjectIdentityPreserved = true, matchingImageState = true,
            automaticXmlSynchronizationVerified = false, codexDesktopQualified = false }), token);
    }

    private static bool IsError(JsonElement value) => value.TryGetProperty("isError", out var error) && error.GetBoolean();

    internal static bool RenderPending(JsonElement result)
    {
        if (!IsError(result)) return false;
        if (result.TryGetProperty("structuredContent", out var structured)) return PendingCode(structured);
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var texts = content.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object
            && x.TryGetProperty("type", out var type) && type.GetString() == "text").ToArray();
        if (texts.Length != 1 || !texts[0].TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return false;
        try { using var decoded = JsonDocument.Parse(text.GetString()!); return PendingCode(decoded.RootElement); }
        catch (JsonException) { return false; }

        static bool PendingCode(JsonElement value) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            && code.GetString() is "native_status_4" or "native_status_7";
    }
    private static JsonElement Success(JsonElement result)
    { Assert.IsFalse(IsError(result), result.GetRawText()); return result; }
    public async ValueTask DisposeAsync() { if (mcp is not null) { await mcp.DisposeAsync(); mcp = null; } }
    private sealed class Design(string name, string epoch, DocumentSpecifier document, string markerId, string markerText, string documentEpoch)
    {
        internal string Name { get; } = name;
        internal string OriginalEpoch { get; } = epoch;
        internal DocumentSpecifier Document { get; } = document;
        internal string MarkerId { get; } = markerId;
        internal string MarkerText { get; } = markerText;
        internal string OriginalDocumentEpoch { get; } = documentEpoch;
        internal bool Reconnected { get; set; }
    }
}
