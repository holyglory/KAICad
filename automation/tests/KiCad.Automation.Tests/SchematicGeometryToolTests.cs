using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicGeometryToolTests
{
    [TestMethod]
    public async Task MissingTargetsAndPreCancellationDoNotAccessRegistry()
    {
        var tools = new SchematicViewTools(null!);
        var contract = typeof(SchematicViewTools).GetMethod(nameof(SchematicViewTools.MeasurePlacement))!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.AreEqual("kicad_schematic_measure_placement", contract.Name);
        Assert.IsTrue(contract.ReadOnly);
        foreach (string request in new[] { "{}", "invalid", "{\"unknown\":true}" })
            Assert.IsTrue((await tools.MeasurePlacement("missing", request, CancellationToken.None)).IsError == true);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => tools.MeasurePlacement("missing", "{}", new(true)));
    }

    [TestMethod]
    public async Task GeometryIsExactAndLimitationsRemainExplicit()
    {
        await WithTool(async (tools, transport, invoke) =>
        {
            var result = await invoke(CancellationToken.None);
            Assert.IsFalse(result.IsError == true);
            var content = result.StructuredContent!.Value;
            Assert.AreEqual("nm", content.GetProperty("distanceUnit").GetString());
            Assert.AreEqual("schematic-sheet", content.GetProperty("coordinateSystem").GetString());
            Assert.IsFalse(content.GetProperty("trackingComplete").GetBoolean());
            Assert.IsFalse(content.GetProperty("nativeDocumentEdited").GetBoolean());
            Assert.IsFalse(content.GetProperty("viewChanged").GetBoolean());
            Assert.AreEqual(transport.Reply, SchematicJson.Parser.Parse<SchematicPlacementGeometry>(content.GetProperty("geometry").GetRawText()));
            transport.Reply.PinGeometryAvailable = false;
            transport.Reply.Obstacles.Clear();
            content = (await invoke(CancellationToken.None)).StructuredContent!.Value;
            Assert.IsFalse(content.GetProperty("pinGeometryAvailable").GetBoolean());
            Assert.IsTrue(content.GetProperty("limitations").GetArrayLength() > 0);
        });
    }

    [TestMethod]
    public async Task RejectsWrongResponseTargetsAndPartialMappings()
    {
        await WithTool(async (_, transport, invoke) =>
        {
            var original = transport.Reply.Clone();
            transport.Reply.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
            Assert.AreEqual("native_observation_mismatch", Code(await invoke(CancellationToken.None)));
            transport.Reply = original.Clone(); transport.Reply.Revision.Sequence++;
            Assert.AreEqual("native_observation_mismatch", Code(await invoke(CancellationToken.None)));
            transport.Reply = original.Clone();
            var mapping = transport.Reply.Obstacles[0].SymbolPins;
            mapping.Complete = false; mapping.Limitations.Add("Exact mapping unavailable.");
            Assert.AreEqual("incomplete_pin_geometry", Code(await invoke(CancellationToken.None)));
            mapping.Pins.Clear();
            var incomplete = await invoke(CancellationToken.None);
            Assert.IsFalse(incomplete.IsError == true);
            var observed = SchematicJson.Parser.Parse<SchematicPlacementGeometry>(incomplete.StructuredContent!.Value.GetProperty("geometry").GetRawText());
            Assert.IsFalse(observed.Obstacles[0].SymbolPins.Complete);
            Assert.AreEqual(0, observed.Obstacles[0].SymbolPins.Pins.Count);
            Assert.AreEqual(1, observed.Obstacles[0].SymbolPins.Limitations.Count);
        });
    }

    [TestMethod]
    public async Task TransportFailureAndCancellationPermitANewObservation()
    {
        await WithTool(async (_, transport, invoke) =>
        {
            transport.Fail = true;
            Assert.AreEqual("native_observation_unavailable", Code(await invoke(CancellationToken.None)));
            transport.Fail = false; transport.Block = true;
            using var cancelled = new CancellationTokenSource();
            var pending = invoke(cancelled.Token);
            await transport.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
            transport.Block = false;
            Assert.IsFalse((await invoke(CancellationToken.None)).IsError == true);
        });
    }

    private static string? Code(CallToolResult result)
    {
        Assert.IsTrue(result.IsError == true);
        using var json = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return json.RootElement.GetProperty("code").GetString();
    }

    private static async Task WithTool(Func<SchematicViewTools, GeometryTransport,
        Func<CancellationToken, Task<CallToolResult>>, Task> action)
    {
        string state = Directory.CreateTempSubdirectory("kicad-geometry-tool-").FullName;
        try
        {
            var transport = new GeometryTransport();
            var registry = new InstanceRegistry(transport, state);
            await registry.AttachAsync("ipc:///tmp/geometry-tool-fixture.sock", transport.Identity.InstanceId);
            var tools = new SchematicViewTools(registry);
            var request = new MeasureSchematicPlacement
            { Document = transport.Reply.Document.Clone(), ExpectedRevision = transport.Reply.Revision.Clone() };
            await action(tools, transport, token => tools.MeasurePlacement(transport.Identity.InstanceId,
                SchematicJson.Formatter.Format(request), token));
        }
        finally { Directory.Delete(state, true); }
    }

    // Contract failure injection only; the compiled native journey proves the
    // same tool against the actual editor, independently of this transport.
    private sealed class GeometryTransport : INativeTransport
    {
        internal readonly NativeClientTests.FixtureTransport Identity = new();
        internal readonly TaskCompletionSource Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Fail, Block;
        internal SchematicPlacementGeometry Reply = new()
        {
            Document = new() { Type = (DocumentType)1,
                SheetPath = new() { Path = { new KIID { Value = Guid.NewGuid().ToString("D") } } } },
            Revision = new() { Epoch = "document-epoch", Sequence = 3 }, PinGeometryAvailable = true,
            Obstacles = { new SchematicPlacementBounds { Id = new() { Value = Guid.NewGuid().ToString("D") },
                SymbolPins = new() { Complete = true, Pins = { new SchematicPinAnchor
                { Id = new() { Value = Guid.NewGuid().ToString("D") }, LibraryPinId = new() { Value = Guid.NewGuid().ToString("D") },
                    Number = "1", Name = "Signal", Position = new() { XNm = -100, YNm = 200 }, BodyDirectionX = 1 } } } } }
        };
        public async Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(request);
            if (!envelope.Message.Is(MeasureSchematicPlacement.Descriptor))
                return await Identity.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            if (Fail) throw new NngException(5, "Injected read failure.");
            if (Block) { Received.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return new ApiResponse { Header = new() { KicadToken = Identity.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(Reply) }.ToByteArray();
        }
    }
}
