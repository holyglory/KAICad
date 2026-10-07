using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

/// <summary>Managed contract coverage for typed schematic cross-probe admission and receipts.</summary>
[TestClass]
public sealed class SchematicCrossProbeToolsContractTests
{
    [TestMethod]
    public async Task TypedSelectionRequiresExactSchematicCheckpointAndKeepsDesignRevisionStable()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid operation = Guid.NewGuid();
        var selection = new SchematicCrossProbeSelectionInput
        {
            Context = SchematicCrossProbeContext.Explicit,
            Mode = SchematicCrossProbeMode.ItemsAndNets,
            FocusItem = new() { Pad = new() { Reference = "U1", Number = "1" } },
            Items = [new() { Footprint = new() { Reference = "U1" } }, new() { SheetPath = [fixture.SheetId] }]
        };

        CallToolResult result = await fixture.Tools.SyncSelection(fixture.InstanceId,
            fixture.DocumentInput, selection, Json(fixture.State), operation, CancellationToken.None);

        Assert.IsFalse(result.IsError ?? false, Text(result));
        JsonElement data = result.StructuredContent!.Value;
        Assert.IsTrue(data.GetProperty("nativeGuiOnly").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("designMutation").GetBoolean(), Text(result));
        Assert.IsTrue(data.GetProperty("selectionChanged").GetBoolean(), Text(result));
        Assert.IsTrue(data.GetProperty("operationReceipt").GetBoolean(), Text(result));
        Assert.AreEqual(operation.ToString("D"), data.GetProperty("operationId").GetString());
        Assert.AreEqual(1, fixture.Peer.SelectionCalls);
        Assert.AreEqual(2, fixture.Peer.LastSelection!.Items.Count);
        Assert.AreEqual(SyncSelectionMode.SsmItemsAndNets, fixture.Peer.LastSelection.Mode);
        Assert.AreEqual(fixture.State, fixture.Peer.LastState);
    }

    [TestMethod]
    public async Task HighlightReplayUsesReceiptAndRejectsOperationReuse()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid operation = Guid.NewGuid();
        var highlight = new SchematicCrossProbeHighlightInput { NetNames = ["/POWER", "/GND"] };

        CallToolResult first = await fixture.Tools.HighlightNets(fixture.InstanceId, fixture.DocumentInput,
            highlight, Json(fixture.State), operation, CancellationToken.None);
        CallToolResult replay = await fixture.Tools.HighlightNets(fixture.InstanceId, fixture.DocumentInput,
            highlight, Json(fixture.State), operation, CancellationToken.None);
        Assert.IsFalse(first.IsError ?? false, Text(first));
        Assert.IsFalse(replay.IsError ?? false, Text(replay));
        Assert.IsTrue(first.StructuredContent!.Value.GetProperty("highlightChanged").GetBoolean(), Text(first));
        Assert.AreEqual(1, fixture.Peer.HighlightCalls, "A completed GUI action is replayed from its receipt.");

        CallToolResult conflict = await fixture.Tools.HighlightNets(fixture.InstanceId, fixture.DocumentInput,
            new() { NetNames = ["/OTHER"] }, Json(fixture.State), operation, CancellationToken.None);
        AssertError(conflict, "operation_id_reused");
        Assert.AreEqual(1, fixture.Peer.HighlightCalls);

        CallToolResult receipt = await fixture.Tools.Receipt(fixture.InstanceId, fixture.DocumentInput,
            fixture.Epoch, operation, CancellationToken.None);
        Assert.IsFalse(receipt.IsError ?? false, Text(receipt));
        Assert.IsTrue(receipt.StructuredContent!.Value.GetProperty("found").GetBoolean(), Text(receipt));
    }

    [TestMethod]
    public async Task StaleEpochRevisionAndWrongTargetAreRefusedBeforeNativeDispatch()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid operation = Guid.NewGuid();
        var valid = new SchematicCrossProbeHighlightInput { NetNames = ["/GND"] };

        DocumentLifecycleState staleRevision = fixture.State.Clone();
        staleRevision.Revision.Sequence++;
        CallToolResult stale = await fixture.Tools.HighlightNets(fixture.InstanceId, fixture.DocumentInput,
            valid, Json(staleRevision), operation, CancellationToken.None);
        AssertError(stale, "schematic_cross_probe_state_changed");
        Assert.AreEqual(0, fixture.Peer.HighlightCalls);

        DocumentLifecycleState staleEpoch = fixture.State.Clone();
        staleEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
        CallToolResult wrongEpoch = await fixture.Tools.HighlightNets(fixture.InstanceId, fixture.DocumentInput,
            valid, Json(staleEpoch), Guid.NewGuid(), CancellationToken.None);
        AssertError(wrongEpoch, "schematic_cross_probe_instance_changed");
        Assert.AreEqual(0, fixture.Peer.HighlightCalls);

        var boardInput = new NativeDocumentInput
        {
            Type = NativeDocumentType.Pcb,
            Project = new() { Name = fixture.Document.Project.Name, Path = fixture.Document.Project.Path },
            BoardFilename = "board.kicad_pcb"
        };
        CallToolResult wrongTarget = await fixture.Tools.HighlightNets(fixture.InstanceId, boardInput,
            valid, Json(fixture.State), Guid.NewGuid(), CancellationToken.None);
        AssertError(wrongTarget, "invalid_schematic_cross_probe_target");
        Assert.AreEqual(0, fixture.Peer.HighlightCalls);
    }

    private static string Json(IMessage message) => SchematicJson.Formatter.Format(message);

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.IsTrue(result.IsError ?? false, Text(result));
        Assert.AreEqual(code, result.StructuredContent!.Value.GetProperty("errorCode").GetString(), Text(result));
        Assert.IsFalse(result.StructuredContent.Value.GetProperty("mutationSubmitted").GetBoolean(), Text(result));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory;
        internal string InstanceId { get; }
        internal string Epoch { get; }
        internal string SheetId { get; }
        internal DocumentSpecifier Document { get; }
        internal NativeDocumentInput DocumentInput { get; }
        internal DocumentLifecycleState State { get; }
        internal Peer Peer { get; }
        internal CrossProbeTools Tools { get; }

        private Fixture(string directory, string instanceId, string epoch, string sheetId,
            DocumentSpecifier document, NativeDocumentInput documentInput,
            DocumentLifecycleState state, Peer peer, CrossProbeTools tools)
        {
            this.directory = directory;
            InstanceId = instanceId; Epoch = epoch; SheetId = sheetId;
            Document = document; DocumentInput = documentInput; State = state;
            Peer = peer; Tools = tools;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            string directory = Directory.CreateTempSubdirectory("kicad-schematic-cross-probe-contract-").FullName;
            string instanceId = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            string sheetId = Guid.NewGuid().ToString("D");
            var document = new DocumentSpecifier
            {
                Type = DocumentType.DoctypeSchematic,
                Project = new() { Name = "cross-probe-contract", Path = directory },
                SheetPath = new() { Path = { new KIID { Value = sheetId } } }
            };
            var documentInput = new NativeDocumentInput
            {
                Type = NativeDocumentType.Schematic,
                Project = new() { Name = document.Project.Name, Path = document.Project.Path },
                SheetPath = new() { Path = [new() { Value = sheetId }] }
            };
            var state = new DocumentLifecycleState
            {
                Document = document.Clone(), Scope = DocumentLifecycleScope.DlsSchematicHierarchy,
                ProcessEpoch = epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 3 },
                ProjectSettingsIncluded = true, DiskBaselineChecked = true,
                StateSha256 = new string('a', 64)
            };
            var peer = new Peer(instanceId, directory, epoch, state);
            var registry = new InstanceRegistry(peer, directory);
            await registry.AttachAsync("ipc:///tmp/kicad-schematic-cross-probe-contract", instanceId);
            return new(directory, instanceId, epoch, sheetId, document, documentInput, state, peer,
                new CrossProbeTools(registry));
        }

        public void Dispose() => Directory.Delete(directory, true);
    }

    private sealed class Peer(string instanceId, string projectPath, string epoch,
        DocumentLifecycleState state) : INativeTransport
    {
        internal int SelectionCalls { get; private set; }
        internal int HighlightCalls { get; private set; }
        internal SyncSelection? LastSelection { get; private set; }
        internal HighlightNets? LastHighlight { get; private set; }
        internal DocumentLifecycleState? LastState { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = ApiRequest.Parser.ParseFrom(bytes).Message;
            if (request.Is(GetAutomationSession.Descriptor))
                return Task.FromResult(Response(new AutomationSession
                {
                    ProtocolVersion = 1, InstanceId = instanceId, ProjectPath = projectPath, Epoch = epoch
                }));
            if (request.Is(ReadDocumentLifecycleState.Descriptor))
            {
                LastState = state.Clone();
                return Task.FromResult(Response(state.Clone()));
            }
            if (request.Is(SyncSelection.Descriptor))
            {
                SelectionCalls++;
                LastSelection = request.Unpack<SyncSelection>();
                return Task.FromResult(Response(new SyncSelectionResponse { Status = CrossProbeStatus.CpsOk }));
            }
            if (request.Is(HighlightNets.Descriptor))
            {
                HighlightCalls++;
                LastHighlight = request.Unpack<HighlightNets>();
                return Task.FromResult(Response(new HighlightNetsResponse { Status = CrossProbeStatus.CpsOk }));
            }
            throw new AssertFailedException($"Unexpected native request: {request.TypeUrl}");
        }

        private byte[] Response(IMessage? message, ApiStatusCode status = ApiStatusCode.AsOk,
            string error = "")
        {
            var response = new ApiResponse
            {
                Header = new() { KicadToken = epoch },
                Status = new() { Status = status, ErrorMessage = error }
            };
            if (message is not null) response.Message = Any.Pack(message);
            return response.ToByteArray();
        }
    }
}
