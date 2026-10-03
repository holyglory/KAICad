using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Board.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbRoutingTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_route_preview", ReadOnly = true),
     Description("Run KiCad's native router on an explicit start item and waypoint sequence, then return typed Track/Arc/Via candidates without committing copper. Set pushAndShove=true to force the detached router snapshot into KiCad's push-and-shove mode. Version 1 previews return the complete create/update/remove plan; removedItemIds are displacement diagnostics. Use drcCandidateRequestJson and drcDeleteRequestJson for DRC, then candidateRequestJson, replacementUpdatesJson and candidateDeleteRequestJson for one checked route commit. Set differentialPair=true for a bounded differential-pair preview. Set persistentSession=true for a single-ended preview session that can be continued, reconciled or cancelled by its operationId; persistent previews remain detached and read-only. Validate any candidates and run detached DRC before a separate route commit."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-pns-router", "board lifecycle state, start item, native PNS waypoint preview"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Preview(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = SchematicJson.Parser.Parse<StartPcbRoutePreview>(requestJson);
        if (request.Document is null || request.Document.Type != DocumentType.DoctypePcb
            || request.ExpectedRevision is null || string.IsNullOrWhiteSpace(request.ProcessEpoch))
            throw new AutomationException("invalid_route_preview", "Supply an explicit PCB, process epoch and expected revision.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (!expected.Document.Equals(request.Document) || expected.Scope != DocumentLifecycleScope.DlsPcb
            || expected.ProcessEpoch != request.ProcessEpoch)
            throw new AutomationException("invalid_route_preview", "The preview request and lifecycle checkpoint target different native state.");
        var result = await registry.Client(instanceId).InvokeAsync<StartPcbRoutePreview, PcbRoutePreviewState>(request, cancellationToken);
        await RequireCompletePreview(instanceId, request.Document, request.OperationId, request.ProcessEpoch,
            result, request.PersistentSession, cancellationToken);
        if (!result.Completed || result.NativeCommit || !result.PreviewOnly)
            throw new AutomationException(string.IsNullOrWhiteSpace(result.ErrorCode) ? "route_preview_failed" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Native routing preview did not complete." : result.ErrorMessage);
        var candidate = new CreateItems { Header = new() { Document = request.Document } };
        candidate.Items.Add(result.RouteItems);
        string? candidateDeleteRequestJson = DeleteRouteCandidate(request.Document, result.RemovedItemIds, result.ReplacementUpdates);
        var after = await registry.Client(instanceId).InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_preview_changed", "The board changed during native routing preview; discard the candidates.");
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = result.ProcessEpoch,
            preview = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)),
            candidateRequestJson = BoardJson.Formatter.Format(candidate),
            candidateDeleteRequestJson,
            drcCandidateRequestJson = BoardJson.Formatter.Format(DrcCandidate(candidate, result)),
            drcDeleteRequestJson = DrcDeletes(request.Document, result),
            replacementUpdatesJson = ReplacementUpdatesJson(result),
            replacementUpdatesUnavailable = ReplacementUpdatesUnavailable(result),
            removedItemIds = result.RemovedItemIds.Select(item => item.Value).ToArray(),
            beforeState = SchematicJson.Formatter.Format(expected), afterState = SchematicJson.Formatter.Format(after),
            nativeRouter = true, persistentSession = request.PersistentSession,
            sessionActive = result.SessionActive, reconciled = result.Reconciled, cancelled = result.Cancelled,
            nativeCommit = false, drcValidated = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_pcb_route_preview_continue", ReadOnly = true),
     Description("Continue one persistent, preview-only native PNS routing session identified by operationId. The request must carry the same PCB, process epoch and revision as the session; no board items are committed. forceFinish=true terminalizes the session after returning the final candidates. A changed board is reconciled as a stale terminal session."),
     KiCadCapability("pcb-routing", "compiled-mcp plus-native-pns-session", "persistent route operation ID, revision and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Continue(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = SchematicJson.Parser.Parse<NativeCapabilityRoutePreviewContinue>(requestJson);
        if (request.Document is null || request.ExpectedRevision is null || string.IsNullOrWhiteSpace(request.ProcessEpoch)
            || string.IsNullOrWhiteSpace(request.OperationId) || request.Waypoints.Count is < 1 or > 256)
            throw new AutomationException("invalid_route_preview_continue", "Supply the session ID, PCB, process epoch, revision and one to 256 waypoints.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (!expected.Document.Equals(request.Document) || expected.Scope != DocumentLifecycleScope.DlsPcb
            || !expected.Revision.Equals(request.ExpectedRevision) || expected.ProcessEpoch != request.ProcessEpoch)
            throw new AutomationException("invalid_route_preview_continue", "The continuation and lifecycle checkpoint target different native state.");
        var result = await registry.Client(instanceId).InvokeAsync<NativeCapabilityRoutePreviewContinue, PcbRoutePreviewState>(request, cancellationToken);
        await RequireCompletePreview(instanceId, request.Document, request.OperationId, request.ProcessEpoch,
            result, result.SessionActive, cancellationToken);
        if (!result.Completed || result.NativeCommit || !result.PreviewOnly)
            throw new AutomationException(string.IsNullOrWhiteSpace(result.ErrorCode) ? "route_preview_continue_failed" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Native route continuation did not complete." : result.ErrorMessage);
        var after = await registry.Client(instanceId).InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!result.Reconciled && !after.Equals(expected))
            throw new AutomationException("route_preview_changed", "The board changed while native routing continued; reconcile the session before using candidates.");
        var candidate = new CreateItems { Header = new() { Document = request.Document } };
        candidate.Items.Add(result.RouteItems);
        string? candidateDeleteRequestJson = DeleteRouteCandidate(request.Document, result.RemovedItemIds, result.ReplacementUpdates);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId = result.OperationId, processEpoch = result.ProcessEpoch,
            preview = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)),
            candidateRequestJson = result.RouteItems.Count == 0 && (result.ReplacementUpdates?.Items.Count ?? 0) == 0
                && result.RemovedItemIds.Count == 0 ? null : BoardJson.Formatter.Format(candidate),
            candidateDeleteRequestJson,
            drcCandidateRequestJson = BoardJson.Formatter.Format(DrcCandidate(candidate, result)),
            drcDeleteRequestJson = DrcDeletes(request.Document, result),
            replacementUpdatesJson = ReplacementUpdatesJson(result),
            replacementUpdatesUnavailable = ReplacementUpdatesUnavailable(result),
            removedItemIds = result.RemovedItemIds.Select(item => item.Value).ToArray(),
            beforeState = SchematicJson.Formatter.Format(expected), afterState = SchematicJson.Formatter.Format(after),
            nativeRouter = true, persistentSession = true, sessionActive = result.SessionActive,
            reconciled = result.Reconciled, cancelled = result.Cancelled, nativeCommit = false, drcValidated = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_pcb_route_preview_cancel"),
     Description("Cancel one persistent native PNS route preview session by exact operation ID, process epoch and PCB. Cancellation discards only the detached preview branch and returns a terminal receipt; it never changes board copper."),
     KiCadCapability("pcb-routing", "compiled-mcp plus-native-pns-session", "persistent route operation ID and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Cancel(string instanceId, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = SchematicJson.Parser.Parse<NativeCapabilityRoutePreviewCancel>(requestJson);
        if (request.Document is null || string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.ProcessEpoch))
            throw new AutomationException("invalid_route_preview_cancel", "Supply the session ID, PCB and process epoch.");
        var result = await registry.Client(instanceId).InvokeAsync<NativeCapabilityRoutePreviewCancel, PcbRoutePreviewState>(request, cancellationToken);
        if (!result.Completed || !result.Cancelled || result.NativeCommit)
            throw new AutomationException(string.IsNullOrWhiteSpace(result.ErrorCode) ? "route_preview_cancel_failed" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Native route preview cancellation did not complete." : result.ErrorMessage);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId = result.OperationId, processEpoch = result.ProcessEpoch,
            receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)),
            cancellationRequested = result.CancellationRequested, cancelled = result.Cancelled,
            sessionActive = result.SessionActive, reconciled = result.Reconciled, nativeCommit = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_pcb_route_preview_read", ReadOnly = true),
     Description("Reconcile or inspect one persistent native PNS route preview session after a lost reply. The exact process epoch, PCB and operation ID are required; a changed board is returned as a stale terminal state and its old candidates are cleared."),
     KiCadCapability("pcb-routing", "compiled-mcp plus-native-pns-session", "persistent route operation ID and process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> ReadSession(string instanceId, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = SchematicJson.Parser.Parse<NativeCapabilityRoutePreviewRead>(requestJson);
        if (request.Document is null || string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.ProcessEpoch))
            throw new AutomationException("invalid_route_preview_read", "Supply the session ID, PCB and process epoch.");
        var result = await registry.Client(instanceId).InvokeAsync<NativeCapabilityRoutePreviewRead, PcbRoutePreviewState>(request, cancellationToken);
        if (result.NativeCommit || !result.PreviewOnly || result.Document is null || !result.Document.Equals(request.Document)
            || result.ProcessEpoch != request.ProcessEpoch || result.OperationId != request.OperationId)
            throw new AutomationException("invalid_route_preview_read", "Native preview inspection returned another session or a non-preview state.");
        bool actionable = result.Completed && result.ErrorCode.Length == 0 && !result.Cancelled && !result.Reconciled
            && CheckedPcbRouteContract.HasPreviewDelta(result) && result.CompleteDeltaVersion == 1;
        if (!actionable && CheckedPcbRouteContract.HasPreviewDelta(result))
        {
            result = result.Clone(); result.RouteItems.Clear(); result.RemovedItemIds.Clear(); result.ReplacementUpdates = null;
            if (result.ErrorCode.Length == 0)
            {
                result.ErrorCode = "route_preview_contract_unsupported";
                result.ErrorMessage = "This inspected session has no actionable complete routing contract; its partial candidates were withheld.";
            }
        }
        CheckedPcbRouteContract.ValidatePreview(result, inspectStateOnly: !actionable);
        var candidate = new CreateItems { Header = new() { Document = request.Document } };
        if (actionable) candidate.Items.Add(result.RouteItems);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId = result.OperationId, processEpoch = result.ProcessEpoch,
            preview = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)),
            candidateRequestJson = actionable ? BoardJson.Formatter.Format(candidate) : null,
            candidateDeleteRequestJson = actionable ? DeleteRouteCandidate(request.Document, result.RemovedItemIds, result.ReplacementUpdates) : null,
            drcCandidateRequestJson = actionable ? BoardJson.Formatter.Format(DrcCandidate(candidate, result)) : null,
            drcDeleteRequestJson = actionable ? DrcDeletes(request.Document, result) : null,
            replacementUpdatesJson = ReplacementUpdatesJson(result),
            replacementUpdatesUnavailable = ReplacementUpdatesUnavailable(result),
            removedItemIds = result.RemovedItemIds.Select(item => item.Value).ToArray(),
            sessionActive = result.SessionActive, reconciled = result.Reconciled, cancelled = result.Cancelled,
            nativeCommit = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_pcb_route_geometry", ReadOnly = true),
     Description("Measure exact native PCB trace, arc and via geometry at one revision checkpoint. Returns net-grouped track length, arc length, layer usage, via transitions and object identities. An optional netName restricts the result. This is numerical routing feedback for placement, high-speed tuning and candidate comparison; it does not certify DRC, impedance, RF or electromagnetic performance and does not mutate the board."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "board lifecycle state, track/arc/via geometry"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Measure(string instanceId, string documentJson, string expectedStateJson,
        CancellationToken cancellationToken, string? netName = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_target", "Route geometry requires an explicit PCB document.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_state", "The expected observation must identify the same PCB and native process epoch.");
        if (netName is not null && string.IsNullOrWhiteSpace(netName))
            throw new AutomationException("invalid_route_net", "If supplied, netName must not be empty.");

        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before measuring route geometry.");
        var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before route geometry could be measured.");
        var response = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
        }, cancellationToken);

        var items = new List<object>();
        var groups = new Dictionary<string, RouteGroup>(StringComparer.Ordinal);
        foreach (var packed in response.Items)
        {
            string itemNet; double lengthNm; string layer; string id;
            string[] viaLayers = [];
            if (packed.Is(Track.Descriptor))
            {
                var track = packed.Unpack<Track>(); itemNet = track.Net.Name; id = track.Id.Value;
                lengthNm = Distance(track.Start.XNm, track.Start.YNm, track.End.XNm, track.End.YNm);
                layer = track.Layer.ToString();
            }
            else if (packed.Is(Arc.Descriptor))
            {
                var arc = packed.Unpack<Arc>(); itemNet = arc.Net.Name; id = arc.Id.Value;
                lengthNm = ArcLength(arc); layer = arc.Layer.ToString();
            }
            else if (packed.Is(Via.Descriptor))
            {
                var via = packed.Unpack<Via>(); itemNet = via.Net.Name; id = via.Id.Value;
                lengthNm = 0; layer = "via";
                viaLayers = via.PadStack?.Layers.Select(value => value.ToString()).ToArray() ?? [];
            }
            else continue;
            if (netName is not null && !string.Equals(itemNet, netName, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(id) || !double.IsFinite(lengthNm))
                throw new AutomationException("invalid_route_geometry", "Native route geometry contained an invalid item identity or length.");
            string safeNet = itemNet ?? "";
            if (!groups.TryGetValue(safeNet, out var group)) groups[safeNet] = group = new(safeNet);
            group.ItemCount++;
            group.TrackLengthNm += lengthNm;
            if (layer == "via") group.ViaCount++;
            else group.Layers.Add(layer);
            items.Add(new { id, netName = safeNet, layer, lengthNm = Math.Round(lengthNm, MidpointRounding.AwayFromZero), viaLayers });
        }
        var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_observation_changed", "The board changed while route geometry was measured; discard the result and retry.");
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document, netFilter = netName,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            routeItems = items, nets = groups.Values.OrderBy(group => group.NetName, StringComparer.Ordinal)
                .Select(group => new { netName = group.NetName, itemCount = group.ItemCount,
                    trackLengthNm = Math.Round(group.TrackLengthNm, MidpointRounding.AwayFromZero),
                    viaCount = group.ViaCount, layers = group.Layers.Order(StringComparer.Ordinal).ToArray() }),
            measuredGeometry = true, drcValidated = false, impedanceValidated = false, nativeCommit = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_pcb_route_commit"),
     Description("Request a native undoable commit of explicit Track/Arc/Via candidates. drcJobStateJson supplies only a job locator: KiCad rereads that job and compares the complete ordered candidate payload before accepting its result. Optional replacementUpdatesJson updates existing copper with its original UUID; removalsJson removes other existing copper inside the same transaction. Use preview drcCandidateRequestJson and drcDeleteRequestJson for the exact ordered candidate and deletion payloads checked by DRC. Creates, updates and removals require distinct UUIDs. Requires fresh, finding-free candidate DRC, an exact serialized PCB state and unchanged native file baselines; the generic numeric revision cursor is not used alone. Older peers without payload verification are rejected."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "validated candidate, exact lifecycle checkpoint, atomic native commit"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> Commit(string instanceId, string requestJson, string expectedStateJson,
     string drcJobStateJson, string operationId, string replacementUpdatesJson = "", string removalsJson = "",
     CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = BoardJson.Parser.Parse<CreateItems>(requestJson);
            var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
            var locator = SchematicJson.Parser.Parse<PcbDrcJobState>(drcJobStateJson);
            UpdateItems? replacementUpdates = string.IsNullOrWhiteSpace(replacementUpdatesJson)
                ? null : BoardJson.Parser.Parse<UpdateItems>(replacementUpdatesJson);
            DeleteItems? removals = string.IsNullOrWhiteSpace(removalsJson)
                ? null : BoardJson.Parser.Parse<DeleteItems>(removalsJson);
            if (!Guid.TryParseExact(operationId, "D", out var op) || op == Guid.Empty)
                throw new AutomationException("invalid_route_operation", "Route commit requires a canonical operation UUID.");
            if (request.Header?.Document is null || expected.Document is null || expected.Scope != DocumentLifecycleScope.DlsPcb
                || !request.Header.Document.Equals(expected.Document)
                || (request.Items.Count == 0 && (replacementUpdates?.Items.Count ?? 0) == 0
                    && (removals?.ItemIds.Count ?? 0) == 0)
                || request.Container is not null || request.Header.Container is not null || request.Header.FieldMask is not null
                || !Guid.TryParseExact(locator.JobId, "D", out var jobId) || jobId == Guid.Empty)
                throw new AutomationException("invalid_route_commit", "Supply a board-level copper candidate, lifecycle checkpoint and retained native DRC job ID.");
            DocumentStateTools.ValidateTarget(expected.Document);
            if (expected.Document.Type != DocumentType.DoctypePcb)
                throw new AutomationException("invalid_route_commit", "Route commits require a PCB document.");
            var client = registry.Client(instanceId);
            if (client.Epoch != expected.ProcessEpoch)
                throw new AutomationException("pcb_instance_changed", "The candidate belongs to another native process epoch.");
            var checkedRequest = new CheckedPcbRouteCommit
            {
                Candidate = request,
                ExpectedState = expected,
                DrcJobId = locator.JobId,
                OperationId = operationId,
                MaximumResultBytes = 4 * 1024 * 1024,
                ReplacementUpdates = replacementUpdates,
                Removals = removals
            };
            // Reject malformed geometry before contacting the native editor.  The
            // native checked controller repeats this admission check, but doing it
            // here gives callers a deterministic typed error and avoids spending a
            // native round trip on a candidate that cannot ever be committed.
            CheckedPcbRouteContract.ValidateRequest(checkedRequest, client.Epoch);
            var receipt = await client.InvokeAsync<CheckedPcbRouteCommit, CheckedPcbRouteReceipt>(checkedRequest, cancellationToken);
            CheckedPcbRouteContract.ValidateResult(checkedRequest, receipt, inspect: false);
            if (receipt.State is not CheckedPcbRouteReceipt.Types.State.Completed
                || !receipt.RequestVerified || receipt.Document is null || !receipt.Document.Equals(expected.Document)
                || receipt.ProcessEpoch != expected.ProcessEpoch || receipt.OperationId != operationId
                || receipt.Before is null || receipt.After is null || receipt.Created is null
                || receipt.Created.CreatedItems.Count != request.Items.Count)
                throw new AutomationException(string.IsNullOrWhiteSpace(receipt.ErrorCode)
                    ? "route_commit_not_completed" : receipt.ErrorCode,
                    string.IsNullOrWhiteSpace(receipt.ErrorMessage) ? receipt.State.ToString() : receipt.ErrorMessage);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId, processEpoch = receipt.ProcessEpoch,
                receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt)),
                beforeState = receipt.Before is null ? null : SchematicJson.Formatter.Format(receipt.Before),
                afterState = receipt.After is null ? null : SchematicJson.Formatter.Format(receipt.After),
                replacementUpdateCount = replacementUpdates?.Items.Count ?? 0,
                removalCount = removals?.ItemIds.Count ?? 0,
                mutationConfirmed = true, atomicTransaction = true, nativeReceipt = true
            });
            return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (NngException error)
        {
            return Failure("route_commit_not_confirmed", $"The native reply was lost after operation {operationId}; inspect the exact route receipt before retrying: {error.Message}");
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or InvalidProtocolBufferException or InvalidJsonException)
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException && error.Message.Contains("tracking", StringComparison.OrdinalIgnoreCase)
                    ? "pcb_tracking_incomplete" : "route_commit_failed";
            return Failure(code, error.Message);
        }
    }

    [McpServerTool(Name = "kicad_pcb_route_receipt", ReadOnly = true),
     Description("Read the native process-owned checked route receipt for an exact operation request. Use after route_commit_not_confirmed; it never submits a mutation and rejects a mismatched operation payload."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-checked-controller", "exact process epoch, operation ID and request payload"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> Receipt(string instanceId, string requestJson, string processEpoch,
        string operationId, CancellationToken cancellationToken)
    {
        try
        {
            var request = SchematicJson.Parser.Parse<CheckedPcbRouteCommit>(requestJson);
            if (request.Candidate is null || request.Candidate.Header?.Document is null
                || !Guid.TryParseExact(request.OperationId, "D", out var op) || op == Guid.Empty
                || string.IsNullOrWhiteSpace(processEpoch))
                throw new AutomationException("invalid_route_receipt", "Supply the exact checked route request, operation ID and process epoch.");
            var document = request.Candidate.Header.Document;
            var read = new ReadCheckedPcbRouteReceipt { Document = document, ProcessEpoch = processEpoch,
                OperationId = operationId, ExpectedRequest = request };
            var result = await registry.Client(instanceId).InvokeAsync<ReadCheckedPcbRouteReceipt, CheckedPcbRouteReceipt>(read, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new { instanceId,
                receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)),
                operationId, readOnly = true });
            return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or InvalidProtocolBufferException or InvalidJsonException)
        { return Failure("route_receipt_failed", error.Message); }
    }

    private sealed class RouteGroup(string netName)
    {
        public string NetName { get; } = netName;
        public int ItemCount { get; set; }
        public int ViaCount { get; set; }
        public double TrackLengthNm { get; set; }
        public HashSet<string> Layers { get; } = new(StringComparer.Ordinal);
    }

    private static double Distance(long startX, long startY, long endX, long endY)
    {
        double dx = (double)endX - startX, dy = (double)endY - startY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double ArcLength(Arc arc)
    {
        double ax = arc.Start.XNm, ay = arc.Start.YNm, bx = arc.Mid.XNm, by = arc.Mid.YNm,
            cx = arc.End.XNm, cy = arc.End.YNm;
        double determinant = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(determinant) < 1e-9)
            return Distance(arc.Start.XNm, arc.Start.YNm, arc.Mid.XNm, arc.Mid.YNm)
                + Distance(arc.Mid.XNm, arc.Mid.YNm, arc.End.XNm, arc.End.YNm);
        double aa = ax * ax + ay * ay, bb = bx * bx + by * by, cc = cx * cx + cy * cy;
        double centerX = (aa * (by - cy) + bb * (cy - ay) + cc * (ay - by)) / determinant;
        double centerY = (aa * (cx - bx) + bb * (ax - cx) + cc * (bx - ax)) / determinant;
        double radius = Distance((long)centerX, (long)centerY, arc.Start.XNm, arc.Start.YNm);
        double start = Math.Atan2(ay - centerY, ax - centerX);
        double middle = Math.Atan2(by - centerY, bx - centerX);
        double end = Math.Atan2(cy - centerY, cx - centerX);
        double ccwStartEnd = PositiveAngle(end - start), ccwStartMiddle = PositiveAngle(middle - start);
        double sweep = ccwStartMiddle <= ccwStartEnd + 1e-10 ? ccwStartEnd : 2 * Math.PI - ccwStartEnd;
        return radius * sweep;
    }

    private async Task RequireCompletePreview(string instanceId, DocumentSpecifier document, string operationId,
        string processEpoch, PcbRoutePreviewState result, bool cleanupUnsupportedSession, CancellationToken token)
    {
        if (!result.Completed || result.ErrorCode.Length != 0)
            throw new AutomationException(result.ErrorCode.Length == 0 ? "route_preview_failed" : result.ErrorCode,
                result.ErrorMessage.Length == 0 ? "The native routing preview did not complete." : result.ErrorMessage);
        if (result.CompleteDeltaVersion != 1 && cleanupUnsupportedSession)
        {
            try
            {
                var cancelled = await registry.Client(instanceId).InvokeAsync<NativeCapabilityRoutePreviewCancel, PcbRoutePreviewState>(new()
                { Document = document, OperationId = operationId, ProcessEpoch = processEpoch }, token);
                if (!cancelled.Completed || cancelled.NativeCommit || !cancelled.PreviewOnly || cancelled.SessionActive || !cancelled.Cancelled
                    || cancelled.Document is null || !cancelled.Document.Equals(document)
                    || cancelled.OperationId != operationId || cancelled.ProcessEpoch != processEpoch)
                    throw new AutomationException("route_preview_cleanup_unconfirmed", "Native cancellation did not confirm the exact detached session was stopped.");
            }
            catch (Exception error) when (error is AutomationException or NativeApiException or NngException or OperationCanceledException)
            {
                throw new AutomationException("route_preview_contract_unsupported_cleanup_unconfirmed",
                    $"Native preview contract {result.CompleteDeltaVersion} is unsupported. Cleanup of detached session {operationId} was not confirmed; inspect or cancel that exact session before retrying. {error.Message}");
            }
        }
        if (result.Document is null || !result.Document.Equals(document) || result.OperationId != operationId || result.ProcessEpoch != processEpoch)
            throw new AutomationException("invalid_route_preview", "Native routing returned a delta for another session or document.");
        CheckedPcbRouteContract.ValidatePreview(result);
    }

    private static string? ReplacementUpdatesJson(PcbRoutePreviewState result)
    {
        return result.ReplacementUpdates is null || result.ReplacementUpdates.Items.Count == 0
            ? null : BoardJson.Formatter.Format(result.ReplacementUpdates);
    }

    private static string? ReplacementUpdatesUnavailable(PcbRoutePreviewState result)
    {
        return string.IsNullOrWhiteSpace(result.ReplacementUpdatesError)
            ? null : result.ReplacementUpdatesError;
    }

    private static string RouteItemId(Any item)
    {
        if (item.Is(Track.Descriptor)) return item.Unpack<Track>().Id.Value;
        if (item.Is(Arc.Descriptor)) return item.Unpack<Arc>().Id.Value;
        if (item.Is(Via.Descriptor)) return item.Unpack<Via>().Id.Value;
        throw new AutomationException("invalid_route_delta", "Native routing returned unsupported copper geometry.");
    }

    private static string? DeleteRouteCandidate(DocumentSpecifier document,
        Google.Protobuf.Collections.RepeatedField<KIID> removed, UpdateItems? updates = null)
    {
        var updatedIds = updates?.Items.Select(RouteItemId).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var request = new DeleteItems { Header = new() { Document = document } };
        request.ItemIds.Add(removed.Where(id => !updatedIds.Contains(id.Value)).Select(id => id.Clone()));
        return request.ItemIds.Count == 0 ? null : BoardJson.Formatter.Format(request);
    }

    private static CreateItems DrcCandidate(CreateItems candidate, PcbRoutePreviewState preview)
    {
        var request = candidate.Clone();
        if (preview.ReplacementUpdates is not null) request.Items.Add(preview.ReplacementUpdates.Items);
        return request;
    }

    private static string? DrcDeletes(DocumentSpecifier document, PcbRoutePreviewState preview)
    {
        var request = new DeleteItems { Header = new() { Document = document } };
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);
        if (preview.ReplacementUpdates is not null)
            foreach (var item in preview.ReplacementUpdates.Items)
            {
                string id = RouteItemId(item);
                if (!updatedIds.Add(id))
                    throw new AutomationException("invalid_route_delta", "Native routing returned duplicate replacement identities.");
                request.ItemIds.Add(new KIID { Value = id });
            }
        request.ItemIds.Add(preview.RemovedItemIds.Where(id => !updatedIds.Contains(id.Value)).Select(id => id.Clone()));
        return request.ItemIds.Count == 0 ? null : BoardJson.Formatter.Format(request);
    }

    private static double PositiveAngle(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_route_geometry_failed",
                errorMessage = error.Message
            });
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
    }

    private static CallToolResult Failure(string code, string message)
    {
        var structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = message });
        return new CallToolResult { IsError = true, StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }
}
