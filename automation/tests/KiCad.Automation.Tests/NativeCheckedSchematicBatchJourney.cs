using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyCheckedSchematicBatch(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<DocumentLifecycleState> State() => client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, token);
        Task<CheckedSchematicBatchReceipt> Apply(CheckedSchematicBatch request) =>
            client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        CheckedSchematicBatch Request(DocumentLifecycleState state, string title)
        {
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), ExpectedRevision = state.Revision.Clone(),
                DocumentEpoch = state.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Checked native batch fixture" };
            batch.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = title } });
            return new() { Batch = batch, ExpectedState = state.Clone() };
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var initial = await State();
        Assert.IsFalse(initial.CompleteChangeTracking, "Full event tracking is still a separate qualification.");
        var stale = Request(initial, "Must not overwrite the later edit");
        var initialTitle = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
            { Document = document, TitleBlock = new() { Title = "Independent native edit" } }, token);
        var current = await State();
        var rejected = await Apply(stale);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, rejected.Status);
        Assert.AreEqual("stale_document_state", rejected.ErrorCode);
        Assert.AreEqual(current, await State());

        // Same revision, stale content digest: counter equality is insufficient.
        var sameCursor = Request(current, "Must not trust a cursor alone");
        sameCursor.ExpectedState.StateSha256 = initial.StateSha256;
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, (await Apply(sameCursor)).Status);
        Assert.AreEqual(current, await State());
        Assert.AreEqual(rejected, await Apply(stale), "A rejected identity must not become a new edit later.");

        var broken = Request(current, "Must roll back with the invalid operation");
        broken.Batch.Operations.Add(new SchematicItemOperation());
        var brokenResult = await Apply(broken);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, brokenResult.Status);
        Assert.AreEqual("native_batch_rejected", brokenResult.ErrorCode);
        Assert.AreEqual(current, await State());

        string projectFile = Path.Combine(document.Project.Path, document.Project.Name + ".kicad_pro");
        byte[] originalFile = await File.ReadAllBytesAsync(projectFile, token);
        try
        {
            // Explicit external-writer fault injection in this disposable fixture.
            await File.AppendAllTextAsync(projectFile, "\n ", token);
            var diskChanged = await State();
            Assert.AreEqual(current.StateSha256, diskChanged.StateSha256);
            Assert.IsTrue(diskChanged.FileBaselines.Any(x => x.Status != NativeFileBaselineStatus.NfbsUnchanged));
            var diskResult = await Apply(Request(diskChanged, "Disk conflict must pause"));
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, diskResult.Status);
            Assert.AreEqual("file_baseline_conflict", diskResult.ErrorCode);
            Assert.AreEqual(diskChanged, await State());
        }
        finally { await File.WriteAllBytesAsync(projectFile, originalFile, token); }
        Assert.AreEqual(current, await State());

        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-checked-mcp"),
            Path.Combine(evidence, instanceId + "-checked-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var observed = await mcp.Tool("kicad_schematic_checked_state", new
            { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
        RequireToolSuccess(observed);
        var combined = SchematicJson.Parser.Parse<CheckedSchematicState>(observed.GetProperty("structuredContent").GetRawText());
        Assert.AreEqual(current, combined.State);
        Assert.AreEqual(current.Revision, combined.Electrical.Hierarchy.Revision);
        Assert.AreEqual(document, combined.Electrical.Hierarchy.Data.Document);
        var standaloneElectrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(
            new() { Document = document.Clone(), SchemaVersion = 9 }, token);
        Assert.AreEqual(standaloneElectrical, combined.Electrical,
            "Combined capture must preserve the same current-schema project metadata as the normal electrical reader.");
        var checkedRequest = Request(current, "Accepted exact-state edit");
        Guid origin = Guid.NewGuid(); checkedRequest.Batch.OriginId = origin.ToString("D");
        var electrical = combined.Electrical;
        var design = ProbeElectricalModel(electrical);
        var recovery = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-checked-recovery.json"));
        var recoveryState = new DesignRecoveryState(origin, Guid.Parse(instanceId),
            new(current.Revision.Epoch, current.Revision.Sequence), electrical.Hierarchy.TrackingComplete,
            design, System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [])), electrical.Hierarchy.Data.Clone(), [],
            PendingMutation: checkedRequest.Batch.Clone(), BaselineElectrical: electrical.Clone(), ObservedElectrical: electrical.Clone(),
            PendingNativeState: current.Clone());
        var pending = recovery.Save(recoveryState, null);
        Assert.AreEqual(DesignRecoveryDisposition.NotFound, (await DesignRecoveryInspector.InspectAsync(recovery, client, token)).Disposition);
        string requestJson = SchematicJson.Formatter.Format(checkedRequest);
        var result = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson });
        RequireToolSuccess(result);
        var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(result.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status);
        Assert.AreEqual(current, receipt.ObservedBefore);
        Assert.AreEqual(receipt.ObservedAfter, await State());
        var recovered = await DesignRecoveryInspector.InspectAsync(new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-checked-recovery.json")), client, token);
        Assert.AreEqual(DesignRecoveryDisposition.CompletedNeedsReconciliation, recovered.Disposition);
        Assert.AreEqual(receipt, recovered.CheckedReceipt); Assert.IsNull(recovered.Receipt);
        Assert.AreEqual(pending.RevisionToken, recovery.Read()!.RevisionToken);
        Assert.AreEqual(current, recovery.Read()!.State.PendingNativeState);
        Assert.IsTrue(receipt.Result.Revision.Sequence > current.Revision.Sequence);
        Assert.IsFalse(result.GetProperty("structuredContent").GetProperty("designFilesSaved").GetBoolean());
        CollectionAssert.AreEqual(originalFile, await File.ReadAllBytesAsync(projectFile, token));

        var duplicate = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson });
        RequireToolSuccess(duplicate);
        Assert.AreEqual(receipt, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(duplicate.GetProperty("structuredContent").GetProperty("receipt").GetRawText()));
        Assert.AreEqual(receipt.ObservedAfter, await State());
        var inspection = await mcp.Tool("kicad_schematic_checked_batch_receipt", new { instanceId, requestJson });
        RequireToolSuccess(inspection);
        Assert.AreEqual(receipt, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(inspection.GetProperty("structuredContent").GetProperty("receipt").GetRawText()));
        Assert.IsFalse(inspection.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean());

        var changed = checkedRequest.Clone(); changed.Batch.Description = "Different checked request";
        Assert.IsTrue((await mcp.Tool("kicad_schematic_apply_checked_batch", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(changed) })).GetProperty("isError").GetBoolean());
        Assert.AreEqual(receipt.ObservedAfter, await State());
        var missing = Request(await State(), "Not sent");
        var notFound = await mcp.Tool("kicad_schematic_checked_batch_receipt", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(missing) });
        RequireToolSuccess(notFound);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsNotFound,
            SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(notFound.GetProperty("structuredContent").GetProperty("receipt").GetRawText()).Status);

        foreach (var (key, title) in new[] { ("z", "Independent native edit"), ("y", "Accepted exact-state edit") })
        {
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, limit.Token)).Title != title)
                await Task.Delay(50, limit.Token);
        }
        await VerifyCheckedObjectEdits(mcp, client, document, processId, display, evidence, instanceId, token);
        // A committed edit remains observable even when saving it was refused.
        // Exercise the actual STDIO recovery surface, not just receipt handlers.
        string recoveryPath = Path.Combine(evidence, instanceId + "-checked-save-recovery.json");
        var saveRecovery = new DesignRecoveryStore(recoveryPath);
        var rejectedSave = new CheckedSaveDocument { Document = document.Clone(),
            OperationId = Guid.NewGuid().ToString("D"), ExpectedState = initial.Clone() };
        var pendingSave = saveRecovery.Save(pending.State with { PendingNativeSave = rejectedSave,
            PendingCandidateFileBytes = pending.State.DesiredFileBytes.ToArray() }, null);
        var refused = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(rejectedSave, token);
        Assert.AreEqual(LifecycleOperationStatus.LosRejected, refused.Status);
        var refusedObservation = await mcp.Tool("kicad_design_recovery_observe", new
            { instanceId, recoveryPath, expectedRevisionToken = pendingSave.RevisionToken });
        RequireToolSuccess(refusedObservation);
        var refusedData = refusedObservation.GetProperty("structuredContent");
        Assert.AreEqual("CompletedNeedsReconciliation", refusedData.GetProperty("disposition").GetString());
        Assert.AreEqual(refused, SchematicJson.Parser.Parse<LifecycleOperationResult>(refusedData.GetProperty("saveReceipt").GetRawText()));
        Assert.AreEqual(pendingSave.RevisionToken, saveRecovery.Read()!.RevisionToken);

        // Retry uses a new, explicitly observed save after the rejected save was
        // inspected. Its receipt is independent of the original schematic batch.
        var acceptedSave = new CheckedSaveDocument { Document = document.Clone(),
            OperationId = Guid.NewGuid().ToString("D"), ExpectedState = await State() };
        pendingSave = saveRecovery.Save(pendingSave.State with { PendingNativeSave = acceptedSave }, pendingSave.RevisionToken);
        var saved = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(acceptedSave, token);
        Assert.AreEqual(LifecycleOperationStatus.LosSaved, saved.Status);
        Assert.AreEqual(saved, await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(acceptedSave, token));
        var savedObservation = await mcp.Tool("kicad_design_recovery_observe", new
            { instanceId, recoveryPath, expectedRevisionToken = pendingSave.RevisionToken });
        RequireToolSuccess(savedObservation);
        var savedData = savedObservation.GetProperty("structuredContent");
        Assert.AreEqual(receipt, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(savedData.GetProperty("checkedReceipt").GetRawText()));
        Assert.AreEqual(saved, SchematicJson.Parser.Parse<LifecycleOperationResult>(savedData.GetProperty("saveReceipt").GetRawText()));
        Assert.AreEqual(pendingSave.RevisionToken, saveRecovery.Read()!.RevisionToken);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-save-observation.json"), savedData.GetRawText(), token);
        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        Assert.AreEqual(observation.Preview.Revision, observation.Snapshot.Revision);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-checked-batch.png"), observation.Preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-receipt.json"), SchematicJson.Formatter.Format(receipt), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-request.json"), requestJson, token);
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = initialTitle }, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        // Write failures and cancellations through the MCP lifecycle tools keep the work safe.
        await VerifySaveFailuresKeepWork(client, document, processId, evidence, instanceId, token);
    }

    // An agent creates, changes and deletes a label, three wires, a junction, a text note and a symbol field only
    // through the compiled STDIO tool, on the rendered editor (ledger p1efcacbeee4e12cc). Every batch names its own
    // operation ID and the exact state it was planned from. Its receipt lists exactly the objects it created, changed
    // or removed; KiCad and its canvas then hold exactly the requested objects; the identical request returns the same
    // receipt without editing again, even after the batch was undone; and one native undo or redo reverses or restores
    // the whole batch. A batch with an invalid operation and a batch planned before another edit are refused without any
    // partial edit, and the agent recovers with a fresh observation and a new operation ID. The native reads here are
    // the test's own independent view of KiCad; every edit goes through the MCP tool.
    private static async Task VerifyCheckedObjectEdits(StdioMcpFixture mcp, NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        string documentJson = SchematicJson.Formatter.Format(document), epoch = "";
        var receipts = new List<CheckedSchematicBatchReceipt>();
        var requests = new List<CheckedSchematicBatch>();
        async Task<CheckedSchematicState> Observe()
        {
            var observed = await mcp.Tool("kicad_schematic_checked_state", new { instanceId, documentJson });
            RequireToolSuccess(observed);
            return SchematicJson.Parser.Parse<CheckedSchematicState>(observed.GetProperty("structuredContent").GetRawText());
        }
        Task<DocumentLifecycleState> State() => client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, token);
        CheckedSchematicBatch Plan(CheckedSchematicState observed, string description, params SchematicItemOperation[] operations)
        {
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), ExpectedRevision = observed.State.Revision.Clone(),
                DocumentEpoch = observed.State.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = description };
            batch.Operations.Add(operations);
            var request = new CheckedSchematicBatch { Batch = batch, ExpectedState = observed.State.Clone() };
            requests.Add(request);
            return request;
        }
        async Task<CheckedSchematicBatchReceipt> Send(CheckedSchematicBatch request, bool inspect = false)
        {
            var reply = await mcp.Tool(inspect ? "kicad_schematic_checked_batch_receipt" : "kicad_schematic_apply_checked_batch",
                new { instanceId, requestJson = SchematicJson.Formatter.Format(request) });
            var data = reply.GetProperty("structuredContent");
            Assert.IsTrue(data.TryGetProperty("receipt", out var json), reply.GetRawText());
            var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(json.GetRawText());
            Assert.AreEqual(!inspect && receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
                reply.TryGetProperty("isError", out var failed) && failed.GetBoolean(), "Only a completed batch reports success: " + reply.GetRawText());
            Assert.AreEqual(!inspect, data.GetProperty("mutationSubmitted").GetBoolean());
            Assert.IsFalse(data.GetProperty("designFilesSaved").GetBoolean());
            Assert.AreEqual(request.Batch.OperationId, receipt.OperationId, "The receipt belongs to exactly this operation ID.");
            Assert.AreEqual(document, receipt.Document);
            return receipt;
        }
        Task<SchematicChangeJournal> Journal(ulong after, CancellationToken limit) =>
            client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = document, DocumentEpoch = epoch, AfterSequence = after }, limit);
        Dictionary<string, Any> Sheet(CheckedSchematicState observed) => ItemsByUuid(observed.Electrical.Hierarchy.Data.Instances
            .Single(screen => Equals(screen.Metadata.Document?.SheetPath, document.SheetPath)).Items);
        async Task Canvas(DocumentLifecycleState state, IReadOnlyDictionary<string, IMessage?> expected, string phase)
        {
            // The editor's own capture of its visible canvas: the committed revision, drawn with exactly these objects.
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
            Assert.AreEqual(state.Revision, observation.Snapshot.Revision, phase + ": the canvas shows the observed revision.");
            Assert.AreEqual(state.Revision, observation.Preview.Revision, phase);
            AssertObjects(ItemsByUuid(observation.Snapshot.Data.Items), expected, phase + " canvas");
            await File.WriteAllBytesAsync(Path.Combine(evidence, $"{instanceId}-crud-{phase.Replace(' ', '-')}.png"),
                observation.Preview.Png.ToByteArray(), token);
        }
        // One native undo or redo in the focused editor is exactly one journal step and reaches exactly the state on the
        // other side of the whole batch.
        async Task<CheckedSchematicState> History(string key, SchematicChange.Types.Kind kind, string digest,
            IReadOnlyDictionary<string, IMessage?> expected, string phase)
        {
            var start = await State();
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicChangeJournal journal;
            while ((journal = await Journal(start.Revision.Sequence, limit.Token)).Changes.Count == 0)
                await Task.Delay(50, limit.Token);
            Assert.AreEqual(1, journal.Changes.Count, phase + ": one native step reverses or restores the whole batch.");
            Assert.AreEqual(kind, journal.Changes[0].Kind, phase);
            var observed = await Observe();
            Assert.AreEqual(digest, observed.State.StateSha256, phase + ": KiCad holds exactly the state on the other side of the batch.");
            AssertObjects(Sheet(observed), expected, phase);
            await Canvas(observed.State, expected, phase);
            return observed;
        }
        async Task<CheckedSchematicState> Commit(CheckedSchematicBatch request, string stage, string[] touched, string[] removed,
            IReadOnlyDictionary<string, IMessage?> before, IReadOnlyDictionary<string, IMessage?> after)
        {
            var receipt = await Send(request);
            receipts.Add(receipt);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, $"{stage}: {receipt.ErrorCode} {receipt.ErrorMessage}");
            Assert.AreEqual(request.ExpectedState, receipt.ObservedBefore, stage);
            CollectionAssert.AreEqual(touched, receipt.Result.Items.Select(ItemUuid).ToArray(), stage + ": the receipt lists every created or changed object.");
            CollectionAssert.AreEqual(removed, receipt.Result.Removed.Select(id => id.Value).ToArray(), stage + ": the receipt lists every removed object.");
            AssertObjects(ItemsByUuid(receipt.Result.Items), after.Where(entry => touched.Contains(entry.Key)).ToDictionary(), stage + " receipt");
            Assert.HasCount(request.Batch.Operations.Count, receipt.Result.OperationTargets);
            Assert.IsTrue(receipt.Result.OperationTargets.All(target => Equals(target, document)), stage);
            var committed = await Observe();
            Assert.AreEqual(receipt.ObservedAfter, committed.State, stage + ": the receipt shows KiCad's state after the batch.");
            Assert.AreNotEqual(request.ExpectedState.StateSha256, committed.State.StateSha256, stage);
            AssertObjects(Sheet(committed), after, stage);
            var journal = await Journal(request.ExpectedState.Revision.Sequence, token);
            Assert.AreEqual(1, journal.Changes.Count, stage + ": the whole batch is one native commit.");
            Assert.AreEqual(SchematicChange.Types.Kind.Commit, journal.Changes[0].Kind, stage);
            Assert.AreEqual(request.Batch.OperationId, journal.Changes[0].OperationId, stage + ": KiCad records the exact operation ID.");
            Assert.AreEqual(request.Batch.Description, journal.Changes[0].Description, stage);
            await Canvas(committed.State, after, stage);

            // The identical request never edits again, neither now nor after the batch was undone.
            Assert.AreEqual(receipt, await Send(request), stage + ": an identical retry returns the same receipt.");
            Assert.AreEqual(committed.State, await State(), stage + ": the retry changed nothing.");
            var undone = await History("z", SchematicChange.Types.Kind.Undo, request.ExpectedState.StateSha256, before, stage + " undo");
            Assert.AreEqual(receipt, await Send(request), stage + ": a retry after undo returns the original receipt.");
            Assert.AreEqual(undone.State, await State(), stage + ": a retry after undo does not apply the batch again.");
            return await History("y", SchematicChange.Types.Kind.Redo, committed.State.StateSha256, after, stage + " redo");
        }

        var initial = await Observe();
        epoch = initial.State.Revision.Epoch;
        var existing = Sheet(initial);
        T Template<T>(MessageDescriptor descriptor, Func<T, bool> usable) where T : IMessage<T>, new() => existing.Values
            .Where(item => item.Is(descriptor)).Select(item => item.Unpack<T>()).Where(usable)
            .OrderBy(item => ItemUuid(Any.Pack(item)), StringComparer.Ordinal).First();
        // New objects start from the sheet's own native objects, so every field is one KiCad itself wrote.
        var labelTemplate = Template<LocalLabel>(LocalLabel.Descriptor, _ => true);
        var wireTemplate = Template<SchematicLine>(SchematicLine.Descriptor, line => line.Type == SchematicLineType.SltWire);
        var textTemplate = Template<SchematicText>(SchematicText.Descriptor, _ => true);
        var symbol = Template<SchematicSymbolInstance>(SchematicSymbolInstance.Descriptor, _ => true);
        Assert.IsFalse(symbol.UserFields.Any(field => field.Name == "CRUD_NOTE"));
        static KIID NewId() => new() { Value = Guid.NewGuid().ToString("D") };
        // Sheet coordinates on KiCad's default 1.27 mm grid, in an empty region of the fixture sheet.
        static Vector2 At(int x, int y) => new() { XNm = x * 1_270_000L, YNm = y * 1_270_000L };
        SchematicLine Wire(Vector2 start, Vector2 end)
        {
            var wire = wireTemplate.Clone(); wire.Id = NewId(); wire.Start = start; wire.End = end; return wire;
        }
        LocalLabel Label(string name, Vector2 position)
        {
            var label = labelTemplate.Clone(); label.Id = NewId(); label.Text.Text_ = name;
            label.Position = position.Clone(); label.Text.Position = position.Clone(); return label;
        }
        SchematicSymbolInstance WithField(string value, long dx)
        {
            var result = symbol.Clone();
            var field = symbol.ValueField.Clone();
            field.Name = "CRUD_NOTE"; field.Text.Text_ = value; field.Visible = true; field.ShowName = false;
            field.Text.Position.XNm += dx; field.Text.Position.YNm += 2_540_000;
            result.UserFields.Add(field);
            return result;
        }
        static SchematicItemOperation Create(IMessage item) => new() { Create = Any.Pack(item) };
        static SchematicItemOperation Update(IMessage item) => new() { Update = Any.Pack(item) };
        static SchematicItemOperation Remove(KIID id) => new() { Remove = id.Clone() };

        // Three wires meeting at one junction, named by a label at the open end, with a note and a symbol field.
        var label = Label("CRUD_NET", At(40, 100));
        SchematicLine west = Wire(At(40, 100), At(50, 100)), east = Wire(At(50, 100), At(60, 100)), south = Wire(At(50, 100), At(50, 110));
        var junction = new Junction { Id = NewId(), Position = At(50, 100), Diameter = new(), Locked = LockedState.LsUnlocked };
        var text = textTemplate.Clone(); text.Id = NewId(); text.Text.Text_ = "Checked CRUD note"; text.Text.Position = At(40, 120);
        var withField = WithField("Checked field", 0);
        // The same objects after one change each.
        var renamed = label.Clone(); renamed.Text.Text_ = "CRUD_RAIL"; renamed.Position = At(44, 100); renamed.Text.Position = At(44, 100);
        var extended = south.Clone(); extended.End = At(50, 115);
        var enlarged = junction.Clone(); enlarged.Diameter = new() { ValueNm = 1_016_000 };
        var edited = text.Clone(); edited.Text.Text_ = "Checked CRUD note, changed"; edited.Text.Position = At(40, 124);
        var fieldChanged = WithField("Checked field, changed", 1_270_000);
        // Objects that only refused batches carry; none of them may ever exist.
        var refusedLabel = Label("REFUSED", At(40, 130));
        var refusedWire = Wire(At(70, 130), At(80, 130));
        var staleText = text.Clone(); staleText.Id = NewId(); staleText.Text.Text_ = "Stale edit";
        Dictionary<string, IMessage?> Expect(IMessage? l, IMessage? w, IMessage? e, IMessage? s, IMessage? j, IMessage? t, IMessage field) => new()
        {
            [label.Id.Value] = l, [west.Id.Value] = w, [east.Id.Value] = e, [south.Id.Value] = s, [junction.Id.Value] = j,
            [text.Id.Value] = t, [symbol.Id.Value] = field,
            [refusedLabel.Id.Value] = null, [refusedWire.Id.Value] = null, [staleText.Id.Value] = null
        };
        var none = Expect(null, null, null, null, null, null, symbol);
        var created = Expect(label, west, east, south, junction, text, withField);
        var changed = Expect(renamed, west, east, extended, enlarged, edited, fieldChanged);
        AssertObjects(existing, none, "before");

        // 1. An invalid batch: two creations and a field edit, then the removal of an object that does not exist.
        var invalid = Plan(initial, "Refused: creations followed by a missing removal", Create(refusedLabel), Create(refusedWire),
            Update(WithField("Refused field", 0)), Remove(NewId()));
        var refused = await Send(invalid);
        receipts.Add(refused);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, refused.Status);
        Assert.AreEqual("native_batch_rejected", refused.ErrorCode);
        StringAssert.StartsWith(refused.ErrorMessage, "Atomic operation 3 rejected:", "The refusal names the operation KiCad refused.");
        Assert.IsNull(refused.Result);
        Assert.AreEqual(initial.State, refused.ObservedBefore);
        Assert.AreEqual(initial.State, refused.ObservedAfter, "The receipt proves KiCad is unchanged.");
        var afterRefusal = await Observe();
        Assert.AreEqual(initial.State, afterRefusal.State, "A refused batch leaves no partial edit, revision or journal step.");
        AssertObjects(Sheet(afterRefusal), none, "refused");
        Assert.IsEmpty((await Journal(initial.State.Revision.Sequence, token)).Changes);
        Assert.AreEqual(refused, await Send(invalid), "Retrying the refused request returns its refusal and never applies it.");
        Assert.AreEqual(refused, await Send(invalid, inspect: true));
        Assert.AreEqual(initial.State, await State());

        // 2. The agent recovers from the refusal with a fresh observation and a new operation ID: one batch creates
        // the label, the wires, the junction, the note and the symbol field.
        var stale = Plan(initial, "Stale: planned before the objects were created", Create(staleText));
        var createdState = await Commit(Plan(afterRefusal, "Create a labelled wire junction, a note and a symbol field",
                Create(label), Create(west), Create(east), Create(south), Create(junction), Create(text), Update(withField)),
            "create", [label.Id.Value, west.Id.Value, east.Id.Value, south.Id.Value, junction.Id.Value, text.Id.Value, symbol.Id.Value],
            [], none, created);

        // 3. A batch planned before that change is refused as stale, changes nothing and shows the current state.
        var staleReceipt = await Send(stale);
        receipts.Add(staleReceipt);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, staleReceipt.Status);
        Assert.AreEqual("stale_document_state", staleReceipt.ErrorCode);
        Assert.AreEqual(createdState.State, staleReceipt.ObservedBefore, "The refusal shows the agent the current state.");
        Assert.IsNull(staleReceipt.Result);
        var afterStale = await Observe();
        Assert.AreEqual(createdState.State, afterStale.State);
        AssertObjects(Sheet(afterStale), created, "stale");
        Assert.AreEqual(staleReceipt, await Send(stale), "A refused stale request never becomes an edit later.");

        // 4. Change every object, 5. then delete them and the field.
        var changedState = await Commit(Plan(afterStale, "Rename the label, extend a wire, enlarge the junction, edit the note and field",
                Update(renamed), Update(extended), Update(enlarged), Update(edited), Update(fieldChanged)),
            "update", [label.Id.Value, south.Id.Value, junction.Id.Value, text.Id.Value, symbol.Id.Value], [], created, changed);
        var deletedState = await Commit(Plan(changedState, "Delete the label, wires, junction, note and symbol field",
                Remove(label.Id), Remove(west.Id), Remove(east.Id), Remove(south.Id), Remove(junction.Id), Remove(text.Id), Update(symbol)),
            "delete", [symbol.Id.Value], [label.Id.Value, west.Id.Value, east.Id.Value, south.Id.Value, junction.Id.Value, text.Id.Value],
            changed, none);
        Assert.AreEqual(initial.State.StateSha256, deletedState.State.StateSha256,
            "Deleting every created object and the field restores exactly the saved form the sheet started with.");
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-crud-requests.json"),
            "[" + string.Join(",\n", requests.Select(request => SchematicJson.Formatter.Format(request))) + "]", token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-crud-receipts.json"),
            "[" + string.Join(",\n", receipts.Select(receipt => SchematicJson.Formatter.Format(receipt))) + "]", token);
        Console.WriteLine($"Checked object edits for {instanceId}: {receipts.Count} receipts, "
            + string.Join(", ", receipts.Select(receipt => $"{receipt.OperationId} {receipt.Status} {receipt.ErrorCode}".TrimEnd())) + ".");
    }

    // The UUID of a native schematic object carried in Any, or null for an object without one.
    private static string? ItemUuid(Any item)
    {
        var type = SchematicText.Descriptor.File.MessageTypes.SingleOrDefault(candidate => item.Is(candidate));
        return type?.FindFieldByName("id") is { FieldType: FieldType.Message } field
            ? (field.Accessor.GetValue(type.Parser.ParseFrom(item.Value)) as KIID)?.Value : null;
    }

    private static Dictionary<string, Any> ItemsByUuid(IEnumerable<Any> items) => items
        .Select(item => (Id: ItemUuid(item), Item: item)).Where(entry => entry.Id is not null).ToDictionary(entry => entry.Id!, entry => entry.Item);

    // Every expected object exists exactly as expected (null: does not exist).
    private static void AssertObjects(IReadOnlyDictionary<string, Any> actual, IReadOnlyDictionary<string, IMessage?> expected, string phase)
    {
        foreach (var (id, message) in expected)
        {
            if (message is null) { Assert.IsFalse(actual.ContainsKey(id), $"{phase}: {id} must not exist."); continue; }
            Assert.IsTrue(actual.TryGetValue(id, out var item) && item.Is(message.Descriptor), $"{phase}: {id} must exist as {message.Descriptor.Name}.");
            Assert.AreEqual(message, message.Descriptor.Parser.ParseFrom(item.Value), $"{phase}: {id} is not exactly the requested object.");
        }
    }
}
