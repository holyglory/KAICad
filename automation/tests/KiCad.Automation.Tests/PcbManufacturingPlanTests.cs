using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Extends the existing manufacturing contract suite at the literal compiled
// STDIO/NNG boundary. The scripted peer proves admission and dispatch mapping,
// not successful native manufacturing or generated artifact correctness.
[TestClass]
public sealed class PcbManufacturingContractTestsPlanning
{
    [TestMethod]
    public async Task CompiledStdioPlanValidatesJobAndRefusesStaleStateWithoutExporting()
    {
        string root = Directory.CreateTempSubdirectory("kmfg-plan-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            using var peer = new PlanningPeer(root);
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), deadline.Token);
            AssertSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = peer.Endpoint, expectedInstanceId = peer.InstanceId }));
            JsonElement tools = (await mcp.ListTools(null)).GetProperty("tools");
            JsonElement schema = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "kicad_pcb_export_plan")
                .GetProperty("inputSchema").GetProperty("properties").GetProperty("input").GetProperty("properties");
            foreach (string field in new[] { "document", "expectedState", "operationId", "outputDirectory", "job" })
                Assert.IsTrue(schema.TryGetProperty(field, out _), schema.GetRawText());
            foreach (string legacy in new[] { "3d", "svg", "dxf", "pdf", "gerbers", "drill", "position" })
                Assert.IsTrue(tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "kicad_pcb_export_" + legacy));

            foreach (string job in new[] { "gerbers", "drill", "position", "svg", "pdf", "dxf", "3d" })
            {
                PcbManufacturingExportInput input = Input(peer, root, job);
                int dispatched = peer.Exports.Count;
                JsonElement plan = Data(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input }));
                Assert.AreEqual("planned", plan.GetProperty("planStatus").GetString(), plan.GetRawText());
                Assert.AreEqual(job, plan.GetProperty("job").GetString(), plan.GetRawText());
                Assert.IsTrue(plan.GetProperty("preflightPassed").GetBoolean());
                Assert.IsFalse(plan.GetProperty("existingOperationFound").GetBoolean());
                Assert.IsFalse(plan.GetProperty("exportDispatched").GetBoolean());
                Assert.IsFalse(plan.GetProperty("operationReserved").GetBoolean());
                Assert.IsFalse(plan.GetProperty("filesWritten").GetBoolean());
                Assert.IsFalse(plan.GetProperty("nativeExecutionVerified").GetBoolean());
                Assert.IsFalse(plan.GetProperty("outputManifestVerified").GetBoolean());
                Assert.AreEqual(dispatched, peer.Exports.Count, "Planning must issue no export command.");
                Assert.IsFalse(Directory.Exists(input.OutputDirectory));
                Assert.IsNull(new PcbExportRecoveryStore(Path.Combine(root, "state")).Read(input.OperationId),
                    "A plan must not reserve a durable export operation.");

                // The real export entry point sends exactly the planned payload.
                // The fixture then fails explicitly without publishing any files.
                JsonElement failed = await mcp.Tool("kicad_pcb_export", new { instanceId = peer.InstanceId, input });
                AssertError(failed, "fixture_export_refused");
                Assert.AreEqual(dispatched + 1, peer.Exports.Count);
                RunPcbManufacturingExport submitted = peer.Exports.Last();
                Assert.AreEqual(plan.GetProperty("normalizedRequest").GetRawText(),
                    JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(submitted)).GetRawText());
                Assert.AreEqual(plan.GetProperty("requestFingerprintSha256").GetString(),
                    Convert.ToHexStringLower(SHA256.HashData(submitted.ToByteArray())));

                JsonElement replayPlan = Data(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input }));
                Assert.AreEqual("existing_operation", replayPlan.GetProperty("planStatus").GetString());
                Assert.IsTrue(replayPlan.GetProperty("existingOperationFound").GetBoolean());
                Assert.AreEqual("failed", replayPlan.GetProperty("existingOutcome").GetString());
                Assert.AreEqual(dispatched + 1, peer.Exports.Count);
            }

            PcbManufacturingExportInput stale = Input(peer, root, "svg");
            peer.ChangeStateOnNextRead = true;
            int beforeStale = peer.Exports.Count;
            AssertError(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input = stale }),
                "pcb_export_state_changed");
            Assert.AreEqual(beforeStale, peer.Exports.Count);
            peer.ResetState();

            PcbManufacturingExportInput changing = Input(peer, root, "pdf");
            peer.ChangeStateOnSecondRead = true;
            AssertError(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input = changing }),
                "pcb_export_state_changed");
            Assert.AreEqual(beforeStale, peer.Exports.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DestinationAndReceiptConflictsNeverDispatchOrReplaceFiles()
    {
        string root = Directory.CreateTempSubdirectory("kmfg-conflicts-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var peer = new PlanningPeer(root);
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), deadline.Token);
            AssertSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = peer.Endpoint, expectedInstanceId = peer.InstanceId }));

            PcbManufacturingExportInput occupied = Input(peer, root, "gerbers");
            await File.WriteAllTextAsync(occupied.OutputDirectory, "preserve this file", deadline.Token);
            JsonElement blocked = Data(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input = occupied }));
            Assert.AreEqual("blocked", blocked.GetProperty("planStatus").GetString());
            CollectionAssert.Contains(Blockers(blocked), "output_path_exists");
            Assert.AreEqual("preserve this file", await File.ReadAllTextAsync(occupied.OutputDirectory, deadline.Token));

            PcbManufacturingExportInput staged = Input(peer, root, "drill");
            string stage = PcbExportRecoveryPolicy.StagingDirectory(staged.OutputDirectory, staged.OperationId);
            Directory.CreateDirectory(stage);
            blocked = Data(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input = staged }));
            CollectionAssert.Contains(Blockers(blocked), "staging_path_exists");
            Assert.IsTrue(Directory.Exists(stage));

            PcbManufacturingExportInput absentParent = Input(peer, root, "position");
            absentParent.OutputDirectory = Path.Combine(root, "missing", "output");
            blocked = Data(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input = absentParent }));
            CollectionAssert.Contains(Blockers(blocked), "output_parent_unavailable");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "missing")));

            PcbManufacturingExportInput conflict = Input(peer, root, "svg");
            peer.MismatchedFingerprint = true;
            AssertError(await mcp.Tool("kicad_pcb_export_plan", new { instanceId = peer.InstanceId, input = conflict }),
                "invalid_pcb_export_receipt");
            Assert.AreEqual(0, peer.Exports.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string[] Blockers(JsonElement plan) => plan.GetProperty("blockers").EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static PcbManufacturingExportInput Input(PlanningPeer peer, string root, string job)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var input = new PcbManufacturingExportInput
        {
            OperationId = Guid.NewGuid().ToString("D"),
            Document = JsonSerializer.Deserialize<NativeDocumentInput>(SchematicJson.Formatter.Format(peer.State.Document), options),
            ExpectedState = JsonSerializer.Deserialize<DocumentLifecycleInput>(SchematicJson.Formatter.Format(peer.State), options),
            OutputDirectory = Path.Combine(root, job + "-" + Guid.NewGuid().ToString("N")),
            Job = job switch
            {
                "gerbers" => new() { ExportGerbers = new() { UseProtelFileExtensions = true } },
                "drill" => new() { ExportDrill = new() { MapFormat = Kiapi.Board.Jobs.DrillMapFormat.DmfSvg } },
                "position" => new() { ExportPosition = new() { ExcludeDnp = true } },
                "svg" => new() { ExportSvg = new() { Precision = 5, FitPageToBoard = true } },
                "pdf" => new() { ExportPdf = new() { PlotSettings = new() { Negative = true } } },
                "dxf" => new() { ExportDxf = new() { PolygonMode = true } },
                "3d" => new() { Export3D = new() { BoardOnly = true } },
                _ => throw new AssertFailedException("Unknown test job")
            }
        };
        return input;
    }

    private static JsonElement Data(JsonElement result)
    {
        AssertSuccess(result);
        return result.GetProperty("structuredContent");
    }

    private static void AssertSuccess(JsonElement result)
    {
        if (result.TryGetProperty("isError", out JsonElement isError))
            Assert.IsFalse(isError.GetBoolean(), result.GetRawText());
    }

    private static void AssertError(JsonElement result, string code)
    {
        Assert.IsTrue(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        Assert.AreEqual(code, result.GetProperty("structuredContent").GetProperty("errorCode").GetString(), result.GetRawText());
    }

    private sealed class PlanningPeer : IDisposable
    {
        private readonly Nng.Socket socket;
        private readonly Task serving;
        private readonly DocumentLifecycleState baseline;
        private readonly Dictionary<string, PcbManufacturingExportReceipt> receipts = [];
        private int stateReads;
        internal string InstanceId { get; } = Guid.NewGuid().ToString("D");
        internal string Endpoint { get; }
        internal DocumentLifecycleState State { get; private set; }
        internal List<RunPcbManufacturingExport> Exports { get; } = [];
        internal bool ChangeStateOnNextRead { get; set; }
        internal bool ChangeStateOnSecondRead { get; set; }
        internal bool MismatchedFingerprint { get; set; }

        internal PlanningPeer(string root)
        {
            Endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "peer.sock"));
            baseline = new()
            {
                Document = new() { Type = DocumentType.DoctypePcb, BoardFilename = "fixture.kicad_pcb",
                    Project = new() { Name = "manufacturing-plan", Path = root } },
                Scope = DocumentLifecycleScope.DlsPcb, ProcessEpoch = Guid.NewGuid().ToString("D"),
                NativeIdentity = Guid.NewGuid().ToString("D"), Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 3 },
                StateSha256 = new string('a', 64), CompleteChangeTracking = true, ProjectSettingsIncluded = true, DiskBaselineChecked = true
            };
            string file = Path.Combine(root, "fixture.kicad_pcb");
            baseline.NativeFiles.Add(file);
            baseline.FileBaselines.Add(new NativeFileBaselineState { Path = file, BaselinePath = file, BaselineKnown = true, CurrentKnown = true,
                Status = NativeFileBaselineStatus.NfbsUnchanged });
            State = baseline.Clone();
            Nng.Check(Nng.nng_rep0_open(out socket));
            try
            {
                Nng.Check(Nng.nng_setopt_ms(socket, "send-timeout", 5000));
                Nng.Check(Nng.nng_listen(socket, Endpoint, IntPtr.Zero, 0));
            }
            catch { Nng.nng_close(socket); throw; }
            serving = Task.Factory.StartNew(Serve, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal void ResetState()
        {
            State = baseline.Clone(); stateReads = 0; ChangeStateOnNextRead = false; ChangeStateOnSecondRead = false;
        }

        private void Serve()
        {
            while (true)
            {
                nuint length = 0;
                if (Nng.nng_recv(socket, out IntPtr pointer, ref length, 1) != 0) return;
                byte[] bytes = new byte[checked((int)length)];
                try { Marshal.Copy(pointer, bytes, 0, bytes.Length); }
                finally { Nng.nng_free(pointer, length); }
                byte[] response = Answer(ApiRequest.Parser.ParseFrom(bytes).Message);
                if (Nng.nng_send(socket, response, (nuint)response.Length, 0) != 0) return;
            }
        }

        private byte[] Answer(Any message)
        {
            IMessage? reply = null;
            if (message.Is(GetAutomationSession.Descriptor))
                reply = new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId,
                    ProjectPath = State.Document.Project.Path, Epoch = State.ProcessEpoch };
            else if (message.Is(ReadDocumentLifecycleState.Descriptor))
            {
                stateReads++;
                if (ChangeStateOnNextRead || ChangeStateOnSecondRead && stateReads == 2)
                { State = State.Clone(); State.Revision.Sequence++; ChangeStateOnNextRead = false; }
                reply = State.Clone();
            }
            else if (message.Is(ReadPcbManufacturingExport.Descriptor))
            {
                var read = message.Unpack<ReadPcbManufacturingExport>();
                receipts.TryGetValue(read.OperationId, out var receipt);
                if (MismatchedFingerprint)
                    receipt = Failure(PcbJobTools.BuildManufacturingRequest(InstanceId, Input(this, State.Document.Project.Path, "svg")));
                if (receipt is not null && MismatchedFingerprint)
                { receipt.OperationId = read.OperationId; receipt.RequestFingerprintSha256 = new string('b', 64); }
                reply = new PcbManufacturingExportReceiptResponse { Found = receipt is not null, Receipt = receipt };
            }
            else if (message.Is(RunPcbManufacturingExport.Descriptor))
            {
                var request = message.Unpack<RunPcbManufacturingExport>();
                Exports.Add(request.Clone());
                var receipt = Failure(request);
                receipts[request.OperationId] = receipt;
                reply = receipt;
            }
            var response = new ApiResponse { Header = new() { KicadToken = State.ProcessEpoch },
                Status = new() { Status = reply is null ? ApiStatusCode.AsUnhandled : ApiStatusCode.AsOk } };
            if (reply is not null) response.Message = Any.Pack(reply);
            return response.ToByteArray();
        }

        private static PcbManufacturingExportReceipt Failure(RunPcbManufacturingExport request) => new()
        {
            OperationId = request.OperationId, ProcessEpoch = request.ProcessEpoch, Document = request.Document.Clone(),
            RequestFingerprintSha256 = Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray())), JobKind = request.JobCase.ToString(),
            Status = PcbManufacturingExportStatus.PcbMfgExportFailed, DispatchStage = PcbManufacturingDispatchStage.PcbMfgDispatchTerminal,
            NativeDispatch = true, Terminal = true, ExpectedState = request.ExpectedState.Clone(), ObservedBefore = request.ExpectedState.Clone(),
            OutputDirectory = request.OutputDirectory, StagingDirectory = PcbExportRecoveryPolicy.StagingDirectory(request.OutputDirectory, request.OperationId),
            PublicationState = PcbManufacturingPublicationState.PcbMfgPublicationFailed,
            ErrorCode = "fixture_export_refused", ErrorMessage = "The scripted peer does not execute manufacturing jobs."
        };

        public void Dispose()
        {
            Nng.nng_close(socket);
            Assert.IsTrue(serving.Wait(TimeSpan.FromSeconds(5)), "The planning peer must release its socket.");
        }
    }
}
