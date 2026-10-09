using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Board.Commands;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    /// <summary>
    /// Qualifies the public via-minimization planner and its checked native
    /// replacement path on a small board with real F.Cu/B.Cu endpoints. The
    /// existing route journey remains independent so this case can isolate
    /// via-only replacement semantics, receipt replay and native history.
    /// </summary>
    private static async Task VerifyNativePcbViaMinimization(NativeClient client,
        DocumentSpecifier schematic, string evidence, string instanceId, int focusProcessId,
        string display, CancellationToken token)
    {
        // KiCad opens only the board matching the attached project root.
        // Preserve that board and project baseline while this fixture runs.
        string boardPath = Path.GetFullPath(Path.Combine(schematic.Project.Path,
            schematic.Project.Name + ".kicad_pcb"));
        string projectPath = Path.GetFullPath(Path.Combine(schematic.Project.Path,
            schematic.Project.Name + ".kicad_pro"));
        bool originalBoardExists = File.Exists(boardPath);
        bool originalProjectExists = File.Exists(projectPath);
        byte[]? originalBoardBytes = originalBoardExists
            ? await File.ReadAllBytesAsync(boardPath, token) : null;
        byte[]? originalProjectBytes = originalProjectExists
            ? await File.ReadAllBytesAsync(projectPath, token) : null;
        string stateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-via-minimize-mcp-").FullName;
        string retained = Path.Combine(evidence, instanceId + "-via-minimization");
        Directory.CreateDirectory(retained);
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            stateDirectory, Path.Combine(retained, "mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach",
            new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        DocumentSpecifier? viaBoard = null;
        try
        {
            await File.WriteAllTextAsync(boardPath, ViaMinimizationBoard(), token);
            var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open");
            DocumentSpecifier board = opened.Document;
            viaBoard = board;

            await SaveCheckedThroughMcp(client, board, evidence, token, verifyReconnect: false);
            DocumentLifecycleState before = await ObserveLifecycleState(client, board, token);
            var position = new Vector2 { XNm = 20_000_000, YNm = 10_000_000 };
            var first = BuildVia("10000000-0000-4000-8000-000000000001", position,
                BoardLayer.BlFCu, BoardLayer.BlIn1Cu, 300_000);
            var second = BuildVia("10000000-0000-4000-8000-000000000002", position,
                BoardLayer.BlIn1Cu, BoardLayer.BlBCu, 300_000);
            var create = new CreateItems { Header = new() { Document = board } };
            create.Items.Add(Any.Pack(first));
            create.Items.Add(Any.Pack(second));
            string createOperation = Guid.NewGuid().ToString("D");
            JsonElement created = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(create),
                expectedStateJson = SchematicJson.Formatter.Format(before), operationId = createOperation
            });
            RequireToolSuccess(created);
            DocumentLifecycleState seeded = StateFromStructured(created);
            Assert.IsGreaterThan(before.Revision.Sequence, seeded.Revision.Sequence, created.GetRawText());

            async Task<(DocumentLifecycleState State, GetItemsResponse Items)> ReadRoutes(DocumentLifecycleState expected)
            {
                var request = new GetItems { Header = new() { Document = board },
                    Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia } };
                JsonElement reply = await mcp.Tool("kicad_pcb_items_read", new
                {
                    instanceId, requestJson = BoardJson.Formatter.Format(request),
                    expectedStateJson = SchematicJson.Formatter.Format(expected)
                });
                RequireToolSuccess(reply);
                JsonElement data = reply.GetProperty("structuredContent");
                var actualState = SchematicJson.Parser.Parse<DocumentLifecycleState>(data.GetProperty("afterState").GetString()!);
                var items = BoardJson.Parser.Parse<GetItemsResponse>(data.GetProperty("response").GetRawText());
                return (actualState, items);
            }

            var seededRead = await ReadRoutes(seeded);
            var sourceIds = new[] { first.Id.Value, second.Id.Value };
            Assert.IsTrue(sourceIds.All(id => seededRead.Items.Items.Any(item => RouteIdentity(item) == id)),
                "The live native board must contain both source vias before planning.");
            var trackSnapshot = seededRead.Items.Items.Where(item => item.Is(Track.Descriptor))
                .ToDictionary(RouteIdentity, item => item.Clone(), StringComparer.Ordinal);
            var candidate = new CreateItems { Header = new() { Document = board } };
            candidate.Items.AddRange(seededRead.Items.Items.Select(item => item.Clone()));

            JsonElement planReply = await mcp.Tool("kicad_pcb_route_via_minimize_plan", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board),
                expectedStateJson = SchematicJson.Formatter.Format(seeded),
                candidateRequestJson = BoardJson.Formatter.Format(candidate),
                requestJson = JsonSerializer.Serialize(new { nets = new[] { "VIA_MAIN" }, maxMerges = 4 })
            });
            RequireToolSuccess(planReply);
            JsonElement plan = planReply.GetProperty("structuredContent");
            Assert.AreEqual(1, plan.GetProperty("mergedCount").GetInt32(), planReply.GetRawText());
            Assert.AreEqual(2, plan.GetProperty("viaCountBefore").GetInt32(), planReply.GetRawText());
            Assert.AreEqual(1, plan.GetProperty("viaCountAfter").GetInt32(), planReply.GetRawText());
            var delete = BoardJson.Parser.Parse<DeleteItems>(plan.GetProperty("candidateDeleteRequestJson").GetString()!);
            var replacement = BoardJson.Parser.Parse<CreateItems>(plan.GetProperty("candidateCreateRequestJson").GetString()!);
            CollectionAssert.AreEquivalent(sourceIds, delete.ItemIds.Select(item => item.Value).ToArray());
            Assert.HasCount(1, replacement.Items);
            Assert.IsTrue(replacement.Items.All(item => item.Is(Via.Descriptor)), "Via minimization apply is Via-only.");
            Via merged = replacement.Items.Single().Unpack<Via>();
            Assert.AreEqual(BoardLayer.BlFCu, merged.PadStack.Drill.StartLayer);
            Assert.AreEqual(BoardLayer.BlBCu, merged.PadStack.Drill.EndLayer);
            Assert.AreEqual(first.PadStack.Drill.Diameter, merged.PadStack.Drill.Diameter);
            Assert.AreEqual(seeded, await ObserveLifecycleState(client, board, token),
                "Planning must not mutate the native board.");

            PcbDrcJobState drc = await DetachedDrc(replacement, delete, seeded);
            string operationId = Guid.NewGuid().ToString("D");
            var applyArgs = new
            {
                instanceId,
                deleteRequestJson = BoardJson.Formatter.Format(delete),
                createRequestJson = BoardJson.Formatter.Format(replacement),
                expectedStateJson = SchematicJson.Formatter.Format(seeded),
                operationId,
                drcJobStateJson = SchematicJson.Formatter.Format(drc)
            };
            JsonElement applied = await mcp.Tool("kicad_pcb_route_via_minimize_apply", applyArgs);
            RequireToolSuccess(applied);
            JsonElement appliedData = applied.GetProperty("structuredContent");
            Assert.IsTrue(appliedData.GetProperty("mutationConfirmed").GetBoolean(), applied.GetRawText());
            Assert.IsTrue(appliedData.GetProperty("nativeReceiptAvailable").GetBoolean(), applied.GetRawText());
            Assert.AreEqual("native-process", appliedData.GetProperty("receiptScope").GetString(), applied.GetRawText());
            Assert.IsFalse(appliedData.GetProperty("replayed").GetBoolean(), applied.GetRawText());
            var after = SchematicJson.Parser.Parse<DocumentLifecycleState>(appliedData.GetProperty("afterState").GetString()!);
            var receipt = SchematicJson.Parser.Parse<CheckedPcbRouteReceipt>(appliedData.GetProperty("receipt").GetRawText());
            var checkedRequest = new CheckedPcbRouteCommit
            {
                Candidate = replacement, Removals = delete, ExpectedState = seeded,
                DrcJobId = drc.JobId, OperationId = operationId, MaximumResultBytes = 4 * 1024 * 1024
            };
            CheckedPcbRouteContract.ValidateResult(checkedRequest, receipt, inspect: false);
            Assert.AreEqual(CheckedPcbRouteReceipt.Types.State.Completed, receipt.State);
            var appliedRead = await ReadRoutes(after);
            Assert.IsTrue(appliedRead.Items.Items.Any(item => RouteIdentity(item) == merged.Id.Value),
                "The generated full-span via must be present after apply.");
            Assert.IsFalse(appliedRead.Items.Items.Any(item => sourceIds.Contains(RouteIdentity(item))),
                "The two source vias must be removed exactly once.");
            foreach (var (id, expectedItem) in trackSnapshot)
                Assert.AreEqual(expectedItem, appliedRead.Items.Items.Single(item => RouteIdentity(item) == id),
                    "Via minimization must not mutate any copper track.");

            JsonElement replay = await mcp.Tool("kicad_pcb_route_via_minimize_apply", applyArgs);
            RequireToolSuccess(replay);
            Assert.IsTrue(replay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(), replay.GetRawText());
            JsonElement receiptReply = await mcp.Tool("kicad_pcb_route_via_minimize_receipt", new
            { instanceId, expectedProcessEpoch = client.Epoch, operationId });
            RequireToolSuccess(receiptReply);
            JsonElement receiptData = receiptReply.GetProperty("structuredContent");
            Assert.IsTrue(receiptData.GetProperty("found").GetBoolean(), receiptReply.GetRawText());
            Assert.AreEqual(operationId, receiptData.GetProperty("operationId").GetString(), receiptReply.GetRawText());

            await WaitForNativeHistory("undo", present: false);
            await WaitForNativeHistory("redo", present: true);
            DocumentLifecycleState redone = await ObserveLifecycleState(client, board, token);
            JsonElement save = await mcp.Tool("kicad_document_save", new
            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(redone), operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(save);
            var saved = SchematicJson.Parser.Parse<DocumentLifecycleState>(save.GetProperty("structuredContent")
                .GetProperty("observedState").GetRawText());
            JsonElement close = await mcp.Tool("kicad_document_close", new
            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(saved), operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(close);
            viaBoard = null;
            var reopened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open");
            board = reopened.Document;
            viaBoard = board;
            var persistedState = await ObserveLifecycleState(client, board, token);
            var persisted = await ReadRoutes(persistedState);
            Assert.IsTrue(persisted.Items.Items.Any(item => RouteIdentity(item) == merged.Id.Value),
                "The checked replacement must persist across save and reopen.");
            foreach (var (id, expectedItem) in trackSnapshot)
                Assert.AreEqual(expectedItem, persisted.Items.Items.Single(item => RouteIdentity(item) == id),
                    "Save/reopen must preserve every original track exactly.");

            // The planner intentionally refuses incompatible profiles by leaving
            // both source vias untouched; an unsafe merged replacement is never
            // emitted and therefore cannot reach checked apply.
            var incompatibleA = BuildVia("10000000-0000-4000-8000-000000000011", position,
                BoardLayer.BlFCu, BoardLayer.BlIn1Cu, 300_000);
            var incompatibleB = BuildVia("10000000-0000-4000-8000-000000000012", position,
                BoardLayer.BlIn1Cu, BoardLayer.BlBCu, 350_000);
            var incompatible = new CreateItems { Header = new() { Document = board },
                Items = { Any.Pack(incompatibleA), Any.Pack(incompatibleB) } };
            var incompatiblePlanReply = await mcp.Tool("kicad_pcb_route_via_minimize_plan", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board),
                expectedStateJson = SchematicJson.Formatter.Format(persistedState),
                candidateRequestJson = BoardJson.Formatter.Format(incompatible),
                requestJson = JsonSerializer.Serialize(new { nets = new[] { "VIA_MAIN" }, maxMerges = 4 })
            });
            RequireToolSuccess(incompatiblePlanReply);
            JsonElement incompatiblePlan = incompatiblePlanReply.GetProperty("structuredContent");
            Assert.AreEqual(0, incompatiblePlan.GetProperty("mergedCount").GetInt32(), incompatiblePlanReply.GetRawText());
            Assert.AreEqual(2, incompatiblePlan.GetProperty("viaCountAfter").GetInt32(), incompatiblePlanReply.GetRawText());
            Assert.IsEmpty(incompatiblePlan.GetProperty("removedItemIds").EnumerateArray(), incompatiblePlanReply.GetRawText());
            var incompatibleCreate = BoardJson.Parser.Parse<CreateItems>(incompatiblePlan.GetProperty("candidateCreateRequestJson").GetString()!);
            Assert.IsEmpty(incompatibleCreate.Items, incompatiblePlanReply.GetRawText());
            Assert.AreEqual(persistedState, await ObserveLifecycleState(client, board, token));

            await File.WriteAllTextAsync(Path.Combine(retained, "via-minimization.json"), JsonSerializer.Serialize(new
            {
                sourceViaIds = sourceIds, mergedViaId = merged.Id.Value, operationId,
                drcJobId = drc.JobId, trackIds = trackSnapshot.Keys.Order(StringComparer.Ordinal).ToArray(),
                replayed = true, undoRedo = true, saveReopen = true, incompatibleProfileRefused = true
            }, new JsonSerializerOptions { WriteIndented = true }), token);

            async Task<PcbDrcJobState> DetachedDrc(CreateItems candidateRequest, DeleteItems deleteRequest,
                DocumentLifecycleState checkpoint)
            {
                JsonElement start = await mcp.Tool("kicad_pcb_drc_start", new
                {
                    instanceId, documentJson = SchematicJson.Formatter.Format(board),
                    operationId = Guid.NewGuid().ToString("D"), refillZones = false,
                    reportAllTrackErrors = true, testFootprints = false,
                    expectedRevisionJson = SchematicJson.Formatter.Format(checkpoint.Revision), processEpoch = client.Epoch,
                    candidateRequestJson = BoardJson.Formatter.Format(candidateRequest),
                    deleteRequestJson = BoardJson.Formatter.Format(deleteRequest)
                });
                RequireToolSuccess(start);
                PcbDrcJobState job = ParseJob(start);
                for (int attempt = 0; attempt < 120 && !job.WorkerFinished; attempt++)
                {
                    await Task.Delay(100, token);
                    JsonElement update = await mcp.Tool("kicad_pcb_drc_job", new
                    {
                        instanceId, documentJson = SchematicJson.Formatter.Format(board), jobId = job.JobId,
                        processEpoch = client.Epoch, candidateRequestJson = BoardJson.Formatter.Format(candidateRequest),
                        deleteRequestJson = BoardJson.Formatter.Format(deleteRequest)
                    });
                    RequireToolSuccess(update);
                    job = ParseJob(update);
                }
                Assert.IsTrue(job.WorkerFinished, SchematicJson.Formatter.Format(job));
                Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, job.Status, job.ErrorMessage);
                Assert.IsTrue(job.CandidateDryRun && job.CandidatePayloadVerified && job.SnapshotComplete && job.ResultsFresh,
                    SchematicJson.Formatter.Format(job));
                Assert.IsEmpty(job.Findings, SchematicJson.Formatter.Format(job));
                return job;
            }

            async Task WaitForNativeHistory(string key, bool present)
            {
                NativeKeyboard.SchematicShortcut(display, focusProcessId, key == "undo" ? "z" : "y",
                    "PCB Editor", true, true, clickFromLeft: 500, clickFromTop: 500);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromSeconds(12));
                while (true)
                {
                    DocumentLifecycleState observed = await ObserveLifecycleState(client, board, wait.Token);
                    var routes = await ReadRoutes(observed);
                    bool found = routes.Items.Items.Any(item => RouteIdentity(item) == merged.Id.Value);
                    if (found == present)
                    {
                        if (!present) Assert.IsTrue(sourceIds.All(id => routes.Items.Items.Any(item => RouteIdentity(item) == id)));
                        else Assert.IsTrue(sourceIds.All(id => routes.Items.Items.All(item => RouteIdentity(item) != id)));
                        return;
                    }
                    await Task.Delay(100, wait.Token);
                }
            }
        }
        finally
        {
            if (viaBoard is not null)
            {
                try
                {
                    RequireToolSuccess(await mcp.Tool("kicad_document_close", new
                    {
                        instanceId,
                        expectedStateJson = SchematicJson.Formatter.Format(
                            await ObserveLifecycleState(client, viaBoard, token)),
                        operationId = Guid.NewGuid().ToString("D")
                    }));
                }
                catch (Exception) { /* Restore the original bytes before reporting the journey failure. */ }
            }
            if (originalBoardExists)
                await File.WriteAllBytesAsync(boardPath, originalBoardBytes!, CancellationToken.None);
            else if (File.Exists(boardPath))
                File.Delete(boardPath);
            if (originalProjectExists)
                await File.WriteAllBytesAsync(projectPath, originalProjectBytes!, CancellationToken.None);
            else if (File.Exists(projectPath))
                File.Delete(projectPath);
            Directory.Delete(stateDirectory, true);
        }

        static PcbDrcJobState ParseJob(JsonElement reply) =>
            SchematicJson.Parser.Parse<PcbDrcJobState>(reply.GetProperty("content").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);

        static DocumentLifecycleState StateFromStructured(JsonElement reply) =>
            SchematicJson.Parser.Parse<DocumentLifecycleState>(reply.GetProperty("structuredContent")
                .GetProperty("afterState").GetString()!);
    }

    private static Via BuildVia(string id, Vector2 position, BoardLayer start, BoardLayer end, long drill) => new()
    {
        Id = new() { Value = id }, Position = position.Clone(), Net = new() { Name = "VIA_MAIN" },
        Type = ViaType.VtBlindBuried, IsFree = false,
        PadStack = new()
        {
            Type = PadStackType.PstCustom, Layers = { start, end },
            Drill = new() { Diameter = new() { XNm = drill, YNm = drill }, StartLayer = start, EndLayer = end, Shape = DrillShape.DsCircle },
            CopperLayers =
            {
                new PadStackLayer { Layer = start, Shape = PadStackShape.PssCircle,
                    CustomAnchorShape = PadStackShape.PssCircle, Size = new() { XNm = 700_000, YNm = 700_000 } },
                new PadStackLayer { Layer = end, Shape = PadStackShape.PssCircle,
                    CustomAnchorShape = PadStackShape.PssCircle, Size = new() { XNm = 700_000, YNm = 700_000 } }
            }
        }
    };

    private static string ViaMinimizationBoard() => """
        (kicad_pcb (version 20260206) (generator "pcbnew")
          (general (thickness 1.6)) (paper "A4")
          (layers (0 "F.Cu" signal) (1 "In1.Cu" signal) (2 "B.Cu" signal) (25 "Edge.Cuts" user))
          (setup (pad_to_mask_clearance 0))
          (net 0 "") (net 1 "VIA_MAIN")
          (gr_rect (start 0 0) (end 40 30) (stroke (width 0.05) (type default)) (fill none)
            (layer "Edge.Cuts") (uuid "10000000-0000-4000-8000-000000000020"))
          (footprint "F_PAD" (layer "F.Cu") (at 10 10) (uuid "10000000-0000-4000-8000-000000000021")
            (attr smd)
            (pad "1" smd circle (at 0 0) (size 1 1) (layers "F.Cu" "F.Paste" "F.Mask")
              (net 1 "VIA_MAIN") (uuid "10000000-0000-4000-8000-000000000022")))
          (footprint "B_PAD" (layer "B.Cu") (at 30 10) (uuid "10000000-0000-4000-8000-000000000023")
            (attr smd)
            (pad "1" smd circle (at 0 0) (size 1 1) (layers "B.Cu" "B.Paste" "B.Mask")
              (net 1 "VIA_MAIN") (uuid "10000000-0000-4000-8000-000000000024")))
          (segment (start 10 10) (end 20 10) (width 0.3) (layer "F.Cu") (net 1)
            (uuid "10000000-0000-4000-8000-000000000025"))
          (segment (start 20 10) (end 30 10) (width 0.3) (layer "B.Cu") (net 1)
            (uuid "10000000-0000-4000-8000-000000000026"))
        )
        """;
}
