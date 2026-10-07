using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

/// <summary>Managed contract coverage for PCB cross-probe admission and error mapping.</summary>
[TestClass]
public sealed class PcbOperationsToolsContractTests
{
    [TestMethod]
    public void NativeMutationReceiptMustMatchOperationProcessAndBoardIdentity()
    {
        const string operationId = "11111111-1111-1111-1111-111111111111";
        const string processEpoch = "22222222-2222-2222-2222-222222222222";
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "board.kicad_pcb",
            Project = new() { Name = "receipt-contract", Path = Path.GetTempPath() }
        };
        var receipt = new NativeCapabilityPcbReadOperationResponse
        {
            Found = true,
            OperationId = operationId,
            Operation = "zone-refill",
            ProcessEpoch = processEpoch,
            Document = document.Clone()
        };

        PcbOperationsContract.ValidateNativeReceiptIdentity(receipt, operationId,
            processEpoch, document, "zone-refill");

        var wrongOperation = receipt.Clone();
        wrongOperation.OperationId = "33333333-3333-3333-3333-333333333333";
        Reject(() => PcbOperationsContract.ValidateNativeReceiptIdentity(wrongOperation,
            operationId, processEpoch, document, "zone-refill"), "wrong_operation_receipt");

        var wrongProcess = receipt.Clone();
        wrongProcess.ProcessEpoch = "44444444-4444-4444-4444-444444444444";
        Reject(() => PcbOperationsContract.ValidateNativeReceiptIdentity(wrongProcess,
            operationId, processEpoch, document, "zone-refill"), "stale_process_epoch");

        var wrongDocument = receipt.Clone();
        wrongDocument.Document.BoardFilename = "other.kicad_pcb";
        Reject(() => PcbOperationsContract.ValidateNativeReceiptIdentity(wrongDocument,
            operationId, processEpoch, document, "zone-refill"), "wrong_operation_document");

        var wrongKind = receipt.Clone();
        wrongKind.Operation = "netlist-import";
        Reject(() => PcbOperationsContract.ValidateNativeReceiptIdentity(wrongKind,
            operationId, processEpoch, document, "zone-refill"), "wrong_operation_receipt");
    }

    [TestMethod]
    public async Task CrossProbeRequiresExactDocumentAndRevisionBeforeDispatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = new SyncSelection
        {
            Context = SyncSelectionContext.SscExplicit,
            Mode = SyncSelectionMode.SsmItemsOnly,
            Items = { new SelectionSpec { Footprint = new() { Reference = "U1" } } }
        };

        var wrongDocument = fixture.State.Clone();
        wrongDocument.Document.BoardFilename = "other.kicad_pcb";
        var wrongDocumentReply = await fixture.Tools.SyncSelection(
            fixture.InstanceId, Json(fixture.Document), Json(request), Json(wrongDocument),
            Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(wrongDocumentReply, "invalid_pcb_operation_state");
        Assert.AreEqual(0, fixture.Peer.CrossProbeCalls);

        var staleRevision = fixture.State.Clone();
        staleRevision.Revision.Sequence++;
        var staleReply = await fixture.Tools.SyncSelection(
            fixture.InstanceId, Json(fixture.Document), Json(request), Json(staleRevision),
            Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(staleReply, "pcb_state_changed");
        Assert.AreEqual(0, fixture.Peer.CrossProbeCalls);

        var accepted = await fixture.Tools.SyncSelection(
            fixture.InstanceId, Json(fixture.Document), Json(request), Json(fixture.State),
            Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.IsFalse(accepted.IsError ?? false, accepted.StructuredContent?.GetRawText());
        Assert.AreEqual(1, fixture.Peer.CrossProbeCalls);
    }

    [TestMethod]
    public async Task SelectionAndHighlightReportGuiOnlyWithoutBoardMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var selection = await fixture.Tools.SyncSelection(
            fixture.InstanceId, Json(fixture.Document),
            Json(new SyncSelection
            {
                Context = SyncSelectionContext.SscExplicit,
                Mode = SyncSelectionMode.SsmItemsAndNets,
                FocusItem = new() { Pad = new() { Reference = "U1", Number = "1" } }
            }), Json(fixture.State), Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.IsFalse(selection.IsError ?? false, selection.StructuredContent?.GetRawText());
        Assert.IsTrue(selection.StructuredContent!.Value.GetProperty("nativeGuiOnly").GetBoolean());
        Assert.IsFalse(selection.StructuredContent.Value.GetProperty("boardMutation").GetBoolean());
        Assert.IsTrue(selection.StructuredContent.Value.GetProperty("selectionChanged").GetBoolean());
        Assert.AreEqual(0, fixture.Peer.MutationCalls);

        var highlight = await fixture.Tools.HighlightNets(
            fixture.InstanceId, Json(fixture.Document),
            Json(new HighlightNets { NetName = { "POWER_RAIL", "GND" } }),
            Json(fixture.State), Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.IsFalse(highlight.IsError ?? false, highlight.StructuredContent?.GetRawText());
        Assert.IsTrue(highlight.StructuredContent!.Value.GetProperty("nativeGuiOnly").GetBoolean());
        Assert.IsFalse(highlight.StructuredContent.Value.GetProperty("boardMutation").GetBoolean());
        Assert.IsTrue(highlight.StructuredContent.Value.GetProperty("selectionChanged").GetBoolean());
        Assert.AreEqual(0, fixture.Peer.MutationCalls);
    }

    [TestMethod]
    public async Task CrossProbeRejectsMalformedTargetsAndRequestsBeforeInstanceLookup()
    {
        using var fixture = await Fixture.CreateAsync();
        string operation = Guid.NewGuid().ToString("D");
        var malformedTarget = await fixture.Tools.SyncSelection(
            Guid.NewGuid().ToString("D"), "{}", "{}", "{}", operation, CancellationToken.None);
        AssertError(malformedTarget, "invalid_pcb_cross_probe_target");

        var schematicTarget = fixture.Document.Clone();
        schematicTarget.Type = DocumentType.DoctypeSchematic;
        var wrongType = await fixture.Tools.HighlightNets(
            Guid.NewGuid().ToString("D"), Json(schematicTarget), Json(new HighlightNets()), "{}",
            Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(wrongType, "invalid_pcb_cross_probe_target");

        var malformedRequest = await fixture.Tools.SyncSelection(
            Guid.NewGuid().ToString("D"), Json(fixture.Document), "{\"mode\":\"SSM_UNKNOWN\"}", "{}",
            Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(malformedRequest, "invalid_pcb_cross_probe_request");

        var malformedSelection = await fixture.Tools.SyncSelection(
            Guid.NewGuid().ToString("D"), Json(fixture.Document),
            "{\"context\":\"SSC_EXPLICIT\",\"mode\":\"SSM_ITEMS_ONLY\",\"items\":[{}]}", "{}",
            Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(malformedSelection, "invalid_pcb_cross_probe_request");
    }

    [TestMethod]
    public async Task StaleProcessEpochIsRefusedBeforeCrossProbeDispatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var stale = fixture.State.Clone();
        stale.ProcessEpoch = Guid.NewGuid().ToString("D");
        var reply = await fixture.Tools.HighlightNets(
            fixture.InstanceId, Json(fixture.Document), Json(new HighlightNets { NetName = { "GND" } }),
            Json(stale), Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(reply, "pcb_instance_changed");
        Assert.AreEqual(0, fixture.Peer.CrossProbeCalls);
    }

    [TestMethod]
    public async Task NativeTokenMismatchMapsToPcbInstanceChanged()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Peer.TokenMismatch = true;
        var reply = await fixture.Tools.SyncSelection(
            fixture.InstanceId, Json(fixture.Document),
            Json(new SyncSelection { Context = SyncSelectionContext.SscExplicit, Mode = SyncSelectionMode.SsmItemsOnly }),
            Json(fixture.State), Guid.NewGuid().ToString("D"), CancellationToken.None);
        AssertError(reply, "pcb_instance_changed");
        Assert.AreEqual(1, fixture.Peer.CrossProbeCalls);
    }

    private static string Json(IMessage message) => SchematicJson.Formatter.Format(message);

    private static void AssertError(CallToolResult result, string expectedCode)
    {
        Assert.IsTrue(result.IsError ?? false, result.StructuredContent?.GetRawText());
        Assert.AreEqual(expectedCode, result.StructuredContent!.Value.GetProperty("errorCode").GetString(),
            result.StructuredContent.Value.GetRawText());
        Assert.IsFalse(result.StructuredContent.Value.GetProperty("mutationSubmitted").GetBoolean());
    }

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory;
        internal string InstanceId { get; }
        internal DocumentSpecifier Document { get; }
        internal DocumentLifecycleState State { get; }
        internal Peer Peer { get; }
        internal PcbOperationsTools Tools { get; }

        private Fixture(string directory, string instanceId, DocumentSpecifier document,
            DocumentLifecycleState state, Peer peer, PcbOperationsTools tools)
        {
            this.directory = directory; InstanceId = instanceId; Document = document;
            State = state; Peer = peer; Tools = tools;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            string directory = Directory.CreateTempSubdirectory("kicad-pcb-cross-probe-contract-").FullName;
            string instanceId = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            var document = new DocumentSpecifier
            {
                Type = DocumentType.DoctypePcb,
                BoardFilename = "board.kicad_pcb",
                Project = new() { Name = "cross-probe-contract", Path = directory }
            };
            var state = new DocumentLifecycleState
            {
                Document = document.Clone(), Scope = DocumentLifecycleScope.DlsPcb,
                ProcessEpoch = epoch, NativeIdentity = Guid.NewGuid().ToString("D"),
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 3 },
                ProjectSettingsIncluded = true, DiskBaselineChecked = true,
                StateSha256 = new string('a', 64)
            };
            var peer = new Peer(instanceId, directory, epoch, state);
            var registry = new InstanceRegistry(peer, directory);
            await registry.AttachAsync("ipc:///tmp/kicad-pcb-cross-probe-contract", instanceId);
            return new(directory, instanceId, document, state, peer, new PcbOperationsTools(registry));
        }

        public void Dispose() => Directory.Delete(directory, true);
    }

    private sealed class Peer(string instanceId, string projectPath, string epoch,
        DocumentLifecycleState state) : INativeTransport
    {
        internal int CrossProbeCalls { get; private set; }
        internal int MutationCalls { get; private set; }
        internal bool TokenMismatch { get; set; }

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
                return Task.FromResult(Response(state.Clone()));
            if (request.Is(SyncSelection.Descriptor))
            {
                CrossProbeCalls++;
                if (TokenMismatch) return Task.FromResult(Response(null, ApiStatusCode.AsTokenMismatch,
                    "The native instance token does not match."));
                return Task.FromResult(Response(new SyncSelectionResponse { Status = CrossProbeStatus.CpsOk }));
            }
            if (request.Is(HighlightNets.Descriptor))
            {
                CrossProbeCalls++;
                if (TokenMismatch) return Task.FromResult(Response(null, ApiStatusCode.AsTokenMismatch,
                    "The native instance token does not match."));
                return Task.FromResult(Response(new HighlightNetsResponse { Status = CrossProbeStatus.CpsOk }));
            }
            MutationCalls++;
            throw new AssertFailedException($"Unexpected native mutation: {request.TypeUrl}");
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
