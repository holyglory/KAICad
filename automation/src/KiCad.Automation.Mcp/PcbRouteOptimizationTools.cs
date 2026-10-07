using System.ComponentModel;
using BigInteger = System.Numerics.BigInteger;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Deterministic proposal-only simplification of safe collinear PCB track chains.</summary>
[McpServerToolType]
public sealed class PcbRouteOptimizationTools(InstanceRegistry registry)
{
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly Dictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_pcb_route_corner_optimize_plan", ReadOnly = true),
     Description("Plan deterministic PCB corner optimization from one exact board observation. candidateRequestJson must be CreateItems containing Track, Arc or Via route objects; requestJson contains optional nets[] and maxMerges. Only same-net, same-layer, same-width Track segments meeting at a degree-two collinear junction are merged. Branches, arcs and vias are preserved. The result is exact DeleteItems and CreateItems protobuf JSON, proposal-only, and does not claim DRC, impedance, length, via minimization or native push-and-shove."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact PCB lifecycle state, deterministic degree-two collinear track simplification"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string candidateRequestJson, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_route_optimize_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_route_optimize_target", "Corner optimization requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_route_optimize_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_route_optimize_state", "expectedStateJson must identify the same PCB and native process epoch.");
        CreateItems candidate = Parse<CreateItems>(candidateRequestJson, "invalid_pcb_route_optimize_candidate");
        if (candidate.Header?.Document is null || !candidate.Header.Document.Equals(document))
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "candidateRequestJson must target the same explicit PCB document.");
        OptimizeRequest request = JsonSerializer.Deserialize<OptimizeRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_pcb_route_optimize", "requestJson must be a JSON object.");
        Validate(request);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before corner optimization planning.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The board changed before corner optimization planning.");

        OptimizationPlan plan = BuildPlan(candidate, request);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("pcb_route_optimize_changed", "The board changed while corner optimization was planned; discard the proposal.");

        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document, planId = plan.PlanId,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            mergedCount = plan.MergedCount, removedItemIds = plan.RemovedItemIds,
            createdTrackIds = plan.CreatedTrackIds, preservedItemCount = plan.PreservedItemCount,
            candidateDeleteRequestJson = BoardJson.Formatter.Format(plan.Delete),
            candidateCreateRequestJson = BoardJson.Formatter.Format(plan.Create),
            proposalOnly = true, nativeCommit = false, drcValidated = false,
            deterministicReplay = true,
            limitations = new[] { "proposal_only", "drc_not_validated", "does_not_reroute", "does_not_minimize_vias", "does_not_push_and_shove" }
        }, JsonOptions);
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    [McpServerTool(Name = "kicad_pcb_route_corner_optimize_apply"),
     Description("Apply one exact route-replacement delete/create pair returned by a checked routing planner in one native undoable transaction. The pair may be a corner-optimization replacement or a headless reroute rip-up candidate; both requests must target the same PCB and expectedStateJson. A supplied drcJobStateJson must be a completed, fresh, finding-free detached DRC receipt whose checked revision and replacement IDs exactly match this request; DRC-bound reroute and tuning use the retained checked route receipt for exact replay. Corner optimization without DRC remains a legacy native transaction until a checked route request can represent it safely."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact corner-optimization delete/create pair, lifecycle checkpoint, native undo transaction, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Apply(string instanceId, string deleteRequestJson, string createRequestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken, string? drcJobStateJson = null) => ExecuteMutation(async () =>
    {
        ValidateOperation(operationId);
        DeleteItems delete = Parse<DeleteItems>(deleteRequestJson, "invalid_pcb_route_optimize_delete");
        CreateItems create = Parse<CreateItems>(createRequestJson, "invalid_pcb_route_optimize_create");
        if (delete.Header?.Document is null || create.Header?.Document is null
            || !delete.Header.Document.Equals(create.Header.Document))
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Delete and create requests must target the same explicit PCB document.");
        DocumentStateTools.ValidateTarget(delete.Header.Document);
        if (delete.ItemIds.Count == 0 || create.Items.Count == 0)
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Supply at least one removed item and one replacement route object.");
        if (delete.ItemIds.Any(item => string.IsNullOrWhiteSpace(item.Value)
            || !Guid.TryParseExact(item.Value, "D", out var deleteId) || deleteId == Guid.Empty
            || delete.ItemIds.Count(source => source.Value == item.Value) != 1))
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Removed item IDs must be distinct canonical UUIDs.");
        if (create.Items.Any(item => !item.Is(Track.Descriptor) && !item.Is(Arc.Descriptor) && !item.Is(Via.Descriptor)))
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "The route replacement accepts only Track, Arc or Via objects.");
        var replacementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Any packed in create.Items)
        {
            string id = packed.Is(Track.Descriptor) ? ValidateReplacementTrack(packed.Unpack<Track>())
                : packed.Is(Arc.Descriptor) ? ValidateReplacementArc(packed.Unpack<Arc>())
                : ValidateReplacementVia(packed.Unpack<Via>());
            if (!replacementIds.Add(id))
                throw new AutomationException("invalid_pcb_route_optimize_candidate", "Replacement route IDs must be distinct.");
            if (delete.ItemIds.Any(item => item.Value == id))
                throw new AutomationException("invalid_pcb_route_optimize_candidate", "Replacement route IDs must not reuse removed item IDs.");
        }
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_route_optimize_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(delete.Header.Document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_route_optimize_state", "expectedStateJson must identify the same PCB and native process epoch.");
        PcbDrcJobState? drc = null;
        string? drcCandidateHash = null;
        if (!string.IsNullOrWhiteSpace(drcJobStateJson))
        {
            drc = Parse<PcbDrcJobState>(drcJobStateJson, "invalid_route_replacement_drc");
            var drcReplacementIds = create.Items.Select(item => item.Is(Track.Descriptor) ? item.Unpack<Track>().Id.Value
                : item.Is(Arc.Descriptor) ? item.Unpack<Arc>().Id.Value : item.Unpack<Via>().Id.Value)
                .Order(StringComparer.Ordinal).ToArray();
            if (!PcbDrcTools.TryGetCandidateReceipt(drc.JobId, out string drcEpoch, out DocumentSpecifier drcDocument,
                    out KiCad.Automation.Protocol.DocumentRevision drcRevision, out string recordedCandidateHash,
                    out IReadOnlyList<string> recordedDeleteIds)
                || !drcDocument.Equals(delete.Header.Document) || drcEpoch != expected.ProcessEpoch
                || drc.Status != PcbDrcJobStatus.PdrcjsCompleted || !drc.WorkerFinished
                || !drc.SnapshotComplete || !drc.ResultsFresh || drc.Findings.Count != 0
                || drc.CheckedRevision is null || !drc.CheckedRevision.Equals(expected.Revision)
                || !drcRevision.Equals(expected.Revision)
                || !recordedDeleteIds.SequenceEqual(delete.ItemIds.Select(item => item.Value).Order(StringComparer.Ordinal))
                || !drc.CandidateItemIds.Order(StringComparer.Ordinal).SequenceEqual(drcReplacementIds, StringComparer.Ordinal))
                throw new AutomationException("invalid_route_replacement_drc", "The detached DRC receipt is not a completed, fresh, finding-free, revision-matched native job bound to this MCP process and replacement geometry.");
            drcCandidateHash = recordedCandidateHash;
            if (!string.Equals(drcCandidateHash, CandidateHash(create), StringComparison.Ordinal))
                throw new AutomationException("invalid_route_replacement_drc", "The replacement geometry differs from the exact candidate submitted to the detached DRC job.");
        }
        string canonicalDelete = BoardJson.Formatter.Format(delete);
        string canonicalCreate = BoardJson.Formatter.Format(create);
        string canonicalExpected = SchematicJson.Formatter.Format(expected);
        string canonicalDrc = drc is null ? "" : SchematicJson.Formatter.Format(drc);
        string fingerprint = instanceId + "\n" + canonicalDelete + "\n" + canonicalCreate + "\n"
            + canonicalExpected + "\n" + canonicalDrc;
        if (drc is not null)
            return await WithNativeReceipt(operationId, fingerprint, async () =>
            {
                CheckedPcbRouteCommit checkedRequest = BuildCheckedRequest(
                    create, delete, expected, drc.JobId, operationId);
                CheckedPcbRouteContract.ValidateRequest(checkedRequest, expected.ProcessEpoch);
                CallToolResult checkedResult = await new PcbRoutingTools(registry).Commit(
                    instanceId, BoardJson.Formatter.Format(checkedRequest.Candidate),
                    SchematicJson.Formatter.Format(checkedRequest.ExpectedState), checkedRequest.DrcJobId,
                    checkedRequest.OperationId, removalsJson: BoardJson.Formatter.Format(checkedRequest.Removals),
                    cancellationToken: cancellationToken);
                return CheckedRouteResult(checkedResult, drc);
            });
        return await WithReceipt(operationId, fingerprint, async () =>
        {
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            if (session.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The corner-optimization candidate belongs to another native process epoch.");
            if (drc is not null)
            {
                var nativeDrcRequest = new ReadPcbDrcJob
                { Document = delete.Header.Document, JobId = drc.JobId, ProcessEpoch = session.Epoch };
                nativeDrcRequest.ExpectedCandidateItems.Add(create.Items);
                PcbDrcJobState nativeDrc = await client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(nativeDrcRequest, cancellationToken);
                if (nativeDrc.Status != PcbDrcJobStatus.PdrcjsCompleted || !nativeDrc.ResultsFresh
                    || nativeDrc.Findings.Count != 0 || nativeDrc.CheckedRevision is null
                    || !nativeDrc.CheckedRevision.Equals(expected.Revision) || !nativeDrc.CandidatePayloadVerified)
                    throw new AutomationException("invalid_route_replacement_drc", "The native DRC job is no longer fresh, clean or revision-matched; rerun DRC before applying.");
            }
            DocumentLifecycleState admission = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = delete.Header.Document }, cancellationToken);
            if (!admission.Equals(expected)) throw new AutomationException("pcb_state_changed", "The board changed before route replacement could be admitted.");
            GetItemsResponse deletedItems = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = delete.Header.Document }, Items = { delete.ItemIds } }, cancellationToken);
            var requestedDeleteIds = delete.ItemIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
            var observedDeleteIds = deletedItems.Items.Select(item => RouteObjectId(item)).Where(id => id is not null)
                .Select(id => id!).ToHashSet(StringComparer.Ordinal);
            if (observedDeleteIds.Count != requestedDeleteIds.Count || !observedDeleteIds.SetEquals(requestedDeleteIds)
                || deletedItems.Items.Any(item => !item.Is(Track.Descriptor) && !item.Is(Arc.Descriptor) && !item.Is(Via.Descriptor)))
                throw new AutomationException("invalid_pcb_route_optimize_candidate", "Every removed item must be an existing Track, Arc or Via at the exact admitted board state.");
            BeginCommitResponse begin = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = delete.Header }, cancellationToken);
            bool committed = false;
            try
            {
                DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = delete.Header.Document }, cancellationToken);
                if (!before.Equals(expected)) throw new AutomationException("pcb_state_changed", "The board changed before route replacement.");
                DeleteItemsResponse deleted = await client.InvokeAsync<DeleteItems, DeleteItemsResponse>(delete, cancellationToken);
                if (deleted.DeletedItems.Any(item => item.Status != ItemDeletionStatus.IdsOk))
                    throw new AutomationException("pcb_route_optimize_rejected", "KiCad rejected one or more source route deletions; the transaction was dropped.");
                CreateItemsResponse created = await client.InvokeAsync<CreateItems, CreateItemsResponse>(create, cancellationToken);
                if (created.CreatedItems.Any(item => item.Status.Code != ItemStatusCode.IscOk))
                    throw new AutomationException("pcb_route_optimize_rejected", "KiCad rejected one or more replacement route objects; the transaction was dropped.");
                await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
                { Id = begin.Id, Action = CommitAction.CmaCommit, Header = delete.Header, Message = "MCP PCB route replacement" }, cancellationToken);
                committed = true;
                DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                    new() { Document = delete.Header.Document }, cancellationToken);
                JsonElement structured = JsonSerializer.SerializeToElement(new
                {
                    instanceId, processEpoch = session.Epoch, operationId, operationOutcome = "completed",
                    beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                    deleted = JsonSerializer.Deserialize<JsonElement>(BoardJson.Formatter.Format(deleted)),
                    created = JsonSerializer.Deserialize<JsonElement>(BoardJson.Formatter.Format(created)),
                    mutationConfirmed = true, atomicTransaction = true, operationReceipt = true,
                    nativeReceiptAvailable = false, receiptScope = "mcp-process", replayableWithinProcess = true,
                    transportRetrySafe = false, drcValidated = drc is not null,
                    drcJobId = drc?.JobId
                }, JsonOptions);
                return new CallToolResult { StructuredContent = structured,
                    Content = [new TextContentBlock { Text = structured.GetRawText() }] };
            }
            finally
            {
                if (!committed)
                {
                    try { await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
                    { Id = begin.Id, Action = CommitAction.CmaDrop, Header = delete.Header }, CancellationToken.None); }
                    catch { }
                }
            }
        });
    });

    [McpServerTool(Name = "kicad_pcb_route_reroute_apply"),
     Description("Apply an exact headless reroute pair: delete the rip-up candidate returned by kicad_pcb_route_plan when includeRipUpCandidate is enabled, then create the DRC-reviewed replacement geometry in one native undoable transaction. This is lifecycle-checked and process-receipted; it does not run DRC itself."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact reroute DeleteItems/CreateItems pair, lifecycle checkpoint, native undo transaction, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> RerouteApply(string instanceId, string deleteRequestJson, string createRequestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken, string? drcJobStateJson = null)
        => string.IsNullOrWhiteSpace(drcJobStateJson)
            ? ExecuteMutation(async () => throw new AutomationException("route_reroute_requires_drc", "Reroute apply requires a completed detached DRC receipt bound to the exact replacement geometry."))
            : Apply(instanceId, deleteRequestJson, createRequestJson, expectedStateJson, operationId, cancellationToken, drcJobStateJson);

    [McpServerTool(Name = "kicad_pcb_route_tune_apply"),
     Description("Apply a length/skew-tuning replacement proposed by kicad_pcb_route_tune_plan. deleteRequestJson identifies the existing route being replaced and createRequestJson contains the exact tuned Track/Arc/Via geometry. The operation uses the same lifecycle, detached-DRC, atomic native transaction and process receipt guarantees as headless reroute; it never accepts an unvalidated tuning candidate."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact tuning replacement delete/create pair, lifecycle checkpoint, native undo transaction, DRC-bound operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> TuneApply(string instanceId, string deleteRequestJson, string createRequestJson,
        string expectedStateJson, string operationId, CancellationToken cancellationToken, string? drcJobStateJson = null)
        => string.IsNullOrWhiteSpace(drcJobStateJson)
            ? ExecuteMutation(async () => throw new AutomationException("route_tune_requires_drc", "Tuning apply requires a completed detached DRC receipt bound to the exact replacement geometry."))
            : Apply(instanceId, deleteRequestJson, createRequestJson, expectedStateJson, operationId, cancellationToken, drcJobStateJson);

    [McpServerTool(Name = "kicad_pcb_route_reroute_receipt", ReadOnly = true),
     Description("Read the process-owned receipt for a headless reroute apply after a lost reply. It never resubmits route geometry and does not replace a native cross-process receipt."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "operation UUID and process-owned reroute receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> RerouteReceipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken)
        => Receipt(instanceId, expectedProcessEpoch, operationId, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_route_tune_receipt", ReadOnly = true),
     Description("Read the process-owned receipt for a DRC-gated PCB length/skew-tuning apply after a lost reply. It never resubmits geometry and does not replace a native cross-process receipt."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "operation UUID and process-owned tuning receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> TuneReceipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken)
        => Receipt(instanceId, expectedProcessEpoch, operationId, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_route_corner_optimize_receipt", ReadOnly = true),
     Description("Read the MCP process-owned receipt for a corner-optimization mutation after a lost reply. It never resubmits geometry and does not replace a native cross-process receipt."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "operation UUID and process-owned corner-optimization receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => ExecuteMutation(async () =>
    {
        ValidateOperation(operationId);
        ValidateEpoch(expectedProcessEpoch);
        if (string.IsNullOrWhiteSpace(instanceId)) throw new AutomationException("invalid_instance_id", "instanceId is required to inspect a route-optimization receipt.");
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (!Receipts.TryGetValue(operationId, out StoredReceipt? stored))
                return ReceiptData(new { instanceId, processEpoch = expectedProcessEpoch, operationId, found = false,
                    receiptScope = "mcp-process", nativeReceiptAvailable = false });
            JsonElement payload = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { });
            string? processEpoch = payload.TryGetProperty("processEpoch", out JsonElement epoch) ? epoch.GetString() : null;
            if (!string.Equals(processEpoch, expectedProcessEpoch, StringComparison.Ordinal)) throw new AutomationException("stale_process_epoch", "The route-optimization receipt belongs to another native process epoch.");
            string? receiptInstance = payload.TryGetProperty("instanceId", out JsonElement instance) ? instance.GetString() : null;
            if (!string.Equals(receiptInstance, instanceId, StringComparison.Ordinal)) throw new AutomationException("wrong_instance_receipt", "The route-optimization receipt belongs to another MCP instance.");
            bool nativeReceiptAvailable = payload.TryGetProperty("nativeReceiptAvailable", out JsonElement native)
                ? native.ValueKind == JsonValueKind.True
                : payload.TryGetProperty("nativeReceipt", out JsonElement nativeReceipt)
                    && nativeReceipt.ValueKind == JsonValueKind.True;
            return ReceiptData(new { instanceId, processEpoch, operationId, found = true,
                receiptScope = nativeReceiptAvailable ? "native-process" : "mcp-process",
                nativeReceiptAvailable, receipt = payload });
        }
        finally { MutationGate.Release(); }
    });

    internal static OptimizationPlan BuildPlan(CreateItems candidate, OptimizeRequest request)
    {
        request.Nets ??= [];
        var tracks = new List<Segment>();
        var preserved = new List<Any>();
        foreach (Any packed in candidate.Items)
        {
            if (packed.Is(Track.Descriptor))
            {
                Track track = packed.Unpack<Track>();
                ValidateTrack(track);
                if (Selected(track.Net.Name, request.Nets)) tracks.Add(new Segment(track.Clone(), true));
                else preserved.Add(Any.Pack(track));
            }
            else if (packed.Is(Arc.Descriptor) || packed.Is(Via.Descriptor))
            {
                preserved.Add(packed.Clone());
            }
            else
            {
                throw new AutomationException("invalid_pcb_route_optimize_candidate", "Candidates may contain only Track, Arc or Via objects.");
            }
        }

        string seed = BoardJson.Formatter.Format(candidate) + "\n" + JsonSerializer.Serialize(request, JsonOptions);
        string planId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        var removed = new HashSet<string>(StringComparer.Ordinal);
        int mergeIndex = 0;
        while (mergeIndex < request.MaxMerges && TryFindMerge(tracks, out int left, out int right, out Vector2 junction))
        {
            Segment first = tracks[left], second = tracks[right];
            Vector2 firstOuter = OtherEndpoint(first.Item, junction);
            Vector2 secondOuter = OtherEndpoint(second.Item, junction);
            Track merged = first.Item.Clone();
            merged.Id = new() { Value = DeterministicId(planId, mergeIndex) };
            merged.Start = firstOuter.Clone();
            merged.End = secondOuter.Clone();
            merged.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_optimization_plan", Value = planId });
            merged.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_optimization_sources", Value = first.Item.Id.Value + "," + second.Item.Id.Value });
            if (first.Existing) removed.Add(first.Item.Id.Value);
            if (second.Existing) removed.Add(second.Item.Id.Value);
            tracks.RemoveAt(Math.Max(left, right));
            tracks.RemoveAt(Math.Min(left, right));
            tracks.Add(new Segment(merged, false));
            mergeIndex++;
        }

        var create = new CreateItems { Header = candidate.Header.Clone() };
        create.Items.AddRange(tracks.Where(segment => !segment.Existing).Select(segment => Any.Pack(segment.Item)));
        var delete = new DeleteItems { Header = candidate.Header.Clone() };
        delete.ItemIds.Add(removed.Order(StringComparer.Ordinal).Select(id => new KIID { Value = id }));
        return new OptimizationPlan(planId, create, delete, mergeIndex,
            removed.Order(StringComparer.Ordinal).ToArray(), tracks.Where(segment => !segment.Existing).Select(segment => segment.Item.Id.Value).Order(StringComparer.Ordinal).ToArray(),
            preserved.Count + tracks.Count);
    }

    internal static CheckedPcbRouteCommit BuildCheckedRequest(CreateItems create, DeleteItems delete,
        DocumentLifecycleState expected, string drcJobId, string operationId)
        => new()
        {
            Candidate = create.Clone(),
            Removals = delete.Clone(),
            ExpectedState = expected.Clone(),
            DrcJobId = drcJobId,
            OperationId = operationId,
            MaximumResultBytes = 4 * 1024 * 1024
        };

    private static bool TryFindMerge(List<Segment> segments, out int left, out int right, out Vector2 junction)
    {
        var incidence = new Dictionary<string, List<(int Index, Vector2 Point)>>(StringComparer.Ordinal);
        for (int index = 0; index < segments.Count; index++)
        {
            Track track = segments[index].Item;
            Add(track.Start, index); Add(track.End, index);
        }
        foreach (var entry in incidence.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (entry.Value.Count != 2) continue;
            var first = entry.Value[0]; var second = entry.Value[1];
            if (first.Index == second.Index) continue;
            Track a = segments[first.Index].Item, b = segments[second.Index].Item;
            if (a.Net.Name != b.Net.Name || a.Layer != b.Layer || a.Width.ValueNm != b.Width.ValueNm) continue;
            Vector2 va = OtherEndpoint(a, first.Point), vb = OtherEndpoint(b, first.Point);
            BigInteger ax = (BigInteger)va.XNm - first.Point.XNm, ay = (BigInteger)va.YNm - first.Point.YNm;
            BigInteger bx = (BigInteger)vb.XNm - first.Point.XNm, by = (BigInteger)vb.YNm - first.Point.YNm;
            BigInteger cross = ax * by - ay * bx;
            BigInteger dot = ax * bx + ay * by;
            if (cross == 0 && dot < 0)
            {
                left = Math.Min(first.Index, second.Index); right = Math.Max(first.Index, second.Index);
                junction = first.Point; return true;
            }
        }
        left = right = -1; junction = new(); return false;

        void Add(Vector2 point, int index)
        {
            string key = PointKey(point);
            if (!incidence.TryGetValue(key, out var values)) incidence[key] = values = [];
            values.Add((index, point));
        }
    }

    private static Vector2 OtherEndpoint(Track track, Vector2 shared)
        => track.Start.Equals(shared) ? track.End : track.Start;

    private static void ValidateTrack(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.Id?.Value) || !Guid.TryParseExact(track.Id.Value, "D", out var id) || id == Guid.Empty
            || string.IsNullOrWhiteSpace(track.Net?.Name) || track.Width.ValueNm <= 0 || track.Start.Equals(track.End))
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Every Track must have a canonical ID, explicit net, positive width and nonzero geometry.");
        if ((int)track.Layer < 3 || (int)track.Layer > 34)
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Tracks must use copper layers.");
    }

    private static string ValidateReplacementTrack(Track track)
    {
        ValidateTrack(track);
        return track.Id.Value;
    }

    private static string ValidateReplacementArc(Arc arc)
    {
        if (string.IsNullOrWhiteSpace(arc.Id?.Value) || !Guid.TryParseExact(arc.Id.Value, "D", out var id) || id == Guid.Empty
            || string.IsNullOrWhiteSpace(arc.Net?.Name) || arc.Width.ValueNm <= 0
            || (int)arc.Layer < 3 || (int)arc.Layer > 34)
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Every Arc must have a canonical ID, explicit net, positive width and copper layer.");
        return arc.Id.Value;
    }

    private static string ValidateReplacementVia(Via via)
    {
        if (string.IsNullOrWhiteSpace(via.Id?.Value) || !Guid.TryParseExact(via.Id.Value, "D", out var id) || id == Guid.Empty
            || string.IsNullOrWhiteSpace(via.Net?.Name) || via.PadStack is null || via.PadStack.Layers.Count < 2)
            throw new AutomationException("invalid_pcb_route_optimize_candidate", "Every Via must have a canonical ID, explicit net and at least two pad-stack layers.");
        return via.Id.Value;
    }

    private static string PointKey(Vector2 point) => point.XNm + "," + point.YNm;
    private static bool Selected(string net, string[] nets) => nets.Length == 0 || nets.Contains(net, StringComparer.Ordinal);
    private static string DeterministicId(string planId, int index)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(planId + ":" + index)).AsSpan(0, 16)).ToString("D");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static void Validate(OptimizeRequest request)
    {
        if (request.Nets is null || request.Nets.Length > 4096 || request.Nets.Any(string.IsNullOrWhiteSpace))
            throw new AutomationException("invalid_pcb_route_optimize", "nets must contain at most 4096 nonempty names.");
        if (request.MaxMerges is < 1 or > 4096)
            throw new AutomationException("invalid_pcb_route_optimize", "maxMerges must be between 1 and 4096.");
    }

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return BoardJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static void ValidateOperation(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical nonempty UUID.");
    }

    private static void ValidateEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid epoch) || epoch == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide the canonical native process epoch.");
    }

    private static string CandidateHash(CreateItems create)
        => PcbDrcTools.CanonicalCandidate(create);

    private static string? RouteObjectId(Any packed)
        => packed.Is(Track.Descriptor) ? packed.Unpack<Track>().Id.Value
        : packed.Is(Arc.Descriptor) ? packed.Unpack<Arc>().Id.Value
        : packed.Is(Via.Descriptor) ? packed.Unpack<Via>().Id.Value : null;

    private static CallToolResult CheckedRouteResult(CallToolResult result, PcbDrcJobState drc)
    {
        if (result.IsError ?? false || result.StructuredContent is null)
            return result;
        JsonElement payload = result.StructuredContent!.Value;

        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId = payload.GetProperty("instanceId").GetString(),
            processEpoch = payload.GetProperty("processEpoch").GetString(),
            operationId = payload.GetProperty("operationId").GetString(),
            operationOutcome = "completed",
            receipt = payload.GetProperty("receipt"),
            beforeState = payload.TryGetProperty("beforeState", out JsonElement before) ? before.GetString() : null,
            afterState = payload.TryGetProperty("afterState", out JsonElement after) ? after.GetString() : null,
            replacementUpdateCount = payload.TryGetProperty("replacementUpdateCount", out JsonElement updates) ? updates.GetInt32() : 0,
            removalCount = payload.TryGetProperty("removalCount", out JsonElement removals) ? removals.GetInt32() : 0,
            mutationConfirmed = true,
            atomicTransaction = true,
            operationReceipt = true,
            nativeReceiptAvailable = true,
            receiptScope = "native-process",
            replayableWithinProcess = true,
            transportRetrySafe = true,
            drcValidated = true,
            drcJobId = drc.JobId,
            checkedRoute = payload
        }, JsonOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private static async Task<CallToolResult> WithReceipt(string operationId, string fingerprint,
        Func<Task<CallToolResult>> action)
    {
        await MutationGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new AutomationException("operation_id_reused", "The operation ID is already bound to a different corner-optimization request.");
                return existing.Result;
            }
            CallToolResult result = await action();
            if (!(result.IsError ?? false)) Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { MutationGate.Release(); }
    }

    private static Task<CallToolResult> ExecuteMutation(Func<Task<CallToolResult>> action) => Execute(action);

    private static CallToolResult ReceiptData(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new CallToolResult { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (NngException error)
        {
            string code = error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached";
            string message = error.RequestDelivered
                ? "KiCad received the route replacement but did not answer; inspect the board and receipt before retrying."
                : "KiCad did not receive the route replacement; no native result was confirmed.";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException or OverflowException)
        {
            string code = error is AutomationException known ? known.Code : "pcb_route_optimize_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private static async Task<CallToolResult> WithNativeReceipt(string operationId, string fingerprint,
        Func<Task<CallToolResult>> action)
    {
        await MutationGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing)
                && existing.Fingerprint != fingerprint)
                throw new AutomationException("operation_id_reused", "The operation ID is already bound to a different route replacement request.");
            CallToolResult result = await action();
            if (!(result.IsError ?? false)) Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { MutationGate.Release(); }
    }

    internal sealed class OptimizeRequest
    {
        public string[] Nets { get; set; } = [];
        public int MaxMerges { get; set; } = 256;
    }

    internal sealed record OptimizationPlan(string PlanId, CreateItems Create, DeleteItems Delete,
        int MergedCount, IReadOnlyList<string> RemovedItemIds, IReadOnlyList<string> CreatedTrackIds, int PreservedItemCount);
    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);
    private sealed record Segment(Track Item, bool Existing);
}
