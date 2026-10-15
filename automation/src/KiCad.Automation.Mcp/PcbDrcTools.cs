using System.ComponentModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Board.Types;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbDrcTools(InstanceRegistry registry)
{
    private static readonly ConcurrentDictionary<string, CandidateReceipt> CandidateReceipts = new(StringComparer.Ordinal);
    private readonly PcbDrcReceiptStore receipts = new(registry.StateDirectory);
    [McpServerTool(Name = "kicad_pcb_drc_start"),
     Description("Start a revision-guarded native PCB DRC job for an explicit PCB document. Requires process epoch, document revision and canonical lowercase operation identity; returned job and revision identities are also canonical lowercase UUIDs. Optional schematic parity checks require the exact observed schematic state. candidateRequestJson may contain explicit Track/Arc/Via items for a detached dry-run qualification; those candidates are never added to the live board. KiCad captures, at the requested revision, every input the enabled tests read: the board with the identity of each of its objects (teardrops included), the project with its design, net-class and text-variable settings, the board's current variant, the custom rules file, the footprint libraries, the drawing sheet, the router settings, exclusions with their comments, the current date or version-control revision when the board's text shows it (also the day an @{...} expression reads with today()), the content of each font file the board's text is laid out with (fonts embedded in the board are part of it) and, with testFootprints, the observed schematic. snapshotComplete is true only when all of them were captured exactly; otherwise inputWarnings says why in an entry that starts with 'snapshot_incomplete: '. A board whose text shows the time of day, which changes while the check runs, is never complete ('snapshot_incomplete: current_time_text'), and neither is one whose text or drawing sheet evaluates an @{...} expression that reads the time, chance or the version-control repository, such as now(), random() or vcsbranch(), or calls a function KiCad does not evaluate from the design alone ('snapshot_incomplete: volatile_text_expression'), also when a variable or another expression builds that call, because the check resolves each text as KiCad lays it out before it looks for calls; a font file the check cannot read gives 'snapshot_incomplete: font_file'. Until a job's worker has finished, snapshotComplete is false with 'snapshot_incomplete: finding_identity_pending', because only a finished check has matched each finding to an object of the open board. Ordinary and candidate jobs follow the same rule. Returns a job snapshot and does not claim completion until kicad_pcb_drc_job reports a terminal result."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "process epoch, document revision, operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public async Task<CallToolResult> Start(string instanceId, string documentJson, string operationId,
        bool refillZones, bool reportAllTrackErrors, bool testFootprints, string expectedRevisionJson,
        string processEpoch, CancellationToken cancellationToken, string? expectedSchematicStateJson = null,
        bool allowDuplicateSheetNames = false, string? candidateRequestJson = null, string? deleteRequestJson = null,
        string? groupChangesJson = null)
    {
        PcbDrcJobState? state = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ParseDocument(documentJson);
            ValidatePcb(document);
            string id = OperationId(operationId);
            var client = registry.Client(instanceId);
            if (processEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "Use the checked process epoch.");
            var expected = SchematicJson.Parser.Parse<Protocol.DocumentRevision>(expectedRevisionJson);
            _ = Identifier(expected.Epoch, "document");
            DocumentLifecycleState? schematic = null;
            if (testFootprints)
            {
                if (string.IsNullOrWhiteSpace(expectedSchematicStateJson))
                    throw new AutomationException("missing_schematic_state", "Observe the source schematic before requesting parity checks.");
                schematic = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedSchematicStateJson);
                ValidateSchematicState(schematic, document, processEpoch);
            }
            else if (expectedSchematicStateJson is not null || allowDuplicateSheetNames)
                throw new AutomationException("unexpected_schematic_options", "Schematic options require parity checking.");
            CreateItems? candidate = null;
            if (!string.IsNullOrWhiteSpace(candidateRequestJson))
            {
                try { candidate = BoardJson.Parser.Parse<CreateItems>(candidateRequestJson); }
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
                    or InvalidOperationException)
                { throw new AutomationException("invalid_drc_candidate", "candidateRequestJson is not a valid board CreateItems request: " + error.Message); }
                ValidateCandidate(candidate, document, allowEmpty: true);
            }
            DeleteItems? delete = null;
            if (!string.IsNullOrWhiteSpace(deleteRequestJson))
            {
                try { delete = BoardJson.Parser.Parse<DeleteItems>(deleteRequestJson); }
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
                    or InvalidOperationException)
                { throw new AutomationException("invalid_drc_delete_candidate", "deleteRequestJson is not a valid board DeleteItems request: " + error.Message); }
                if (delete.Header?.Document is null || !delete.Header.Document.Equals(document) || delete.ItemIds.Count == 0
                    || delete.ItemIds.Any(item => !CanonicalUuid(item.Value))
                    || delete.ItemIds.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count() != delete.ItemIds.Count)
                    throw new AutomationException("invalid_drc_delete_candidate", "deleteRequestJson must target this PCB with distinct canonical route IDs.");
                candidate ??= new CreateItems { Header = new() { Document = document } };
            }
            var groupChanges = ParseGroupChanges(groupChangesJson, "invalid_drc_group_changes");
            if (groupChanges.Count != 0 && candidate is null)
                throw new AutomationException("invalid_drc_group_changes", "Grouped route DRC requires the exact route candidate and removal payload.");
            if (candidate is not null && candidate.Items.Count == 0 && delete is null)
                throw new AutomationException("invalid_drc_candidate", "A detached route check requires at least one create, replacement or removal.");
            string canonicalDocument = SchematicJson.Formatter.Format(document);
            string canonicalRevision = SchematicJson.Formatter.Format(expected);
            string canonicalCandidate = candidate is null ? "" : BoardJson.Formatter.Format(candidate);
            string canonicalDelete = delete is null ? "" : BoardJson.Formatter.Format(delete);
            string canonicalGroups = CanonicalGroupChanges(groupChanges);
            string candidateHash = candidate is null ? "" : CanonicalCandidate(candidate);
            string fingerprint = PcbDrcReceiptStore.Fingerprint(instanceId, id, processEpoch,
                canonicalDocument, canonicalRevision, canonicalCandidate, canonicalDelete, canonicalGroups,
                expectedSchematicStateJson, refillZones, reportAllTrackErrors, testFootprints,
                allowDuplicateSheetNames);
            // Hold the per-operation lease across admission, native dispatch and
            // terminal journal write. A second MCP process cannot race this
            // operation into a duplicate native DRC request.
            using var receiptLease = receipts.Acquire(instanceId, id);
            var reservation = receipts.ReserveHeld(instanceId, id, processEpoch, canonicalDocument,
                canonicalRevision, canonicalCandidate, canonicalDelete, canonicalGroups, fingerprint, candidateHash);
            if (!reservation.Created && reservation.Receipt.Outcome == PcbDrcReceiptOutcome.Completed)
            {
                if (reservation.Receipt.ResultJson is null)
                    throw new AutomationException("drc_receipt_corrupt", "A completed PCB DRC receipt has no retained result.");
                try { state = SchematicJson.Parser.Parse<PcbDrcJobState>(reservation.Receipt.ResultJson); }
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
                { throw new AutomationException("drc_receipt_corrupt", "The retained PCB DRC result is invalid: " + error.Message); }
                ValidateJobState(state, document, processEpoch);
                RestoreCandidateReceipt(reservation.Receipt, state);
                return reservation.Receipt.ResultJson;
            }
            var request = new StartPcbDrcJob
            {
                Document = document,
                OperationId = id,
                RefillZones = refillZones,
                ReportAllTrackErrors = reportAllTrackErrors,
                TestFootprints = testFootprints,
                ExpectedRevision = expected,
                ProcessEpoch = processEpoch,
                ExpectedSchematicState = schematic,
                AllowDuplicateSheetNames = allowDuplicateSheetNames
            };
            if (candidate is not null) request.CandidateItems.Add(candidate.Items);
            if (delete is not null) request.DeleteItemIds.Add(delete.ItemIds);
            request.GroupChanges.Add(groupChanges);
            try
            {
                state = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(request, cancellationToken);
            }
            catch (NngException error)
            {
                receipts.MarkUnknownHeld(instanceId, id, error.RequestDelivered,
                    error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached", error.Message);
                if (error.RequestDelivered)
                    throw new AutomationException("operation_outcome_unknown",
                        "KiCad received the PCB DRC request but did not answer; inspect its operation receipt before retrying.");
                throw;
            }
            catch (OperationCanceledException error)
            {
                receipts.MarkUnknownHeld(instanceId, id, null, "operation_outcome_unknown", error.Message);
                throw;
            }
            catch (Exception error) when (error is NativeApiException or AutomationException
                or InvalidProtocolBufferException or InvalidJsonException)
            {
                // A native reply that cannot be validated is evidence that the
                // request reached KiCad, but never evidence that it was absent.
                receipts.MarkUnknownHeld(instanceId, id, true, "operation_outcome_unknown", error.Message);
                throw;
            }
            try
            {
                ValidateJobState(state, document, client.Epoch);
                if (state.OperationId != id || !expected.Equals(state.CheckedRevision))
                    throw new AutomationException("invalid_drc_job_state", "Native DRC admission does not match the requested operation and revision.");
                if (!Equals(schematic, state.CheckedSchematicState))
                    throw new AutomationException("invalid_drc_job_state", "Native DRC admission does not match the requested source schematic.");
                if (state.CandidateDryRun != (candidate is not null)
                    || state.CandidateItemIds.Count != (candidate?.Items.Count ?? 0))
                    throw new AutomationException("invalid_drc_job_state", "Native DRC did not preserve the detached candidate identity contract.");
            }
            catch (Exception error) when (error is NativeApiException or AutomationException
                or InvalidProtocolBufferException or InvalidJsonException)
            {
                receipts.MarkUnknownHeld(instanceId, id, true, "operation_outcome_unknown", error.Message);
                throw;
            }
            if (candidate is not null)
            {
                string startedJobId = state.JobId;
                string startedOperationId = state.OperationId;
                var startedRevision = state.CheckedRevision.Clone();
                var startedSchematic = state.CheckedSchematicState?.Clone();
                string[] startedCandidateIds = state.CandidateItemIds.ToArray();
                // Starting a detached candidate check records the candidate in
                // the native job, but the start reply itself is not the native
                // payload-comparison receipt. Read it back with the complete
                // ordered Any payload before exposing the state so fast jobs
                // cannot appear unverified merely because no polling round ran.
                // The native read handler performs payload comparison from the
                // expected-candidate field; send the same request through the
                // typed client rather than trusting IDs or the start snapshot.
                try
                {
                    var verificationRequest = new NativeCapabilityPcbDrcReplacementJob
                    { Document = document, JobId = state.JobId, ProcessEpoch = client.Epoch };
                    verificationRequest.ExpectedCandidateItems.Add(candidate.Items);
                    if (delete is not null)
                        verificationRequest.ExpectedDeleteItemIds.Add(delete.ItemIds);
                    verificationRequest.ExpectedGroupChanges.Add(groupChanges);
                    var verified = await client.InvokeAsync<NativeCapabilityPcbDrcReplacementJob, PcbDrcJobState>(verificationRequest, cancellationToken);
                    ValidateJobState(verified, document, client.Epoch);
                    if (verified.JobId != startedJobId
                        || verified.OperationId != startedOperationId
                        || !startedRevision.Equals(verified.CheckedRevision)
                        || !Equals(startedSchematic, verified.CheckedSchematicState)
                        || !verified.CandidateDryRun
                        || !verified.CandidatePayloadVerified
                        || !verified.CandidateItemIds.SequenceEqual(startedCandidateIds, StringComparer.Ordinal))
                        throw new AutomationException("invalid_drc_candidate_receipt",
                            "Native DRC candidate verification returned a different job, operation, revision, source or candidate identity.");
                    state = verified;
                }
                catch (NngException error)
                {
                    receipts.MarkUnknownHeld(instanceId, id, true, "operation_outcome_unknown", error.Message);
                    throw new AutomationException("operation_outcome_unknown",
                        "KiCad started the PCB DRC candidate but its payload receipt was not returned; inspect the operation receipt before retrying.");
                }
                catch (OperationCanceledException error)
                {
                    receipts.MarkUnknownHeld(instanceId, id, true, "operation_outcome_unknown", error.Message);
                    throw;
                }
                catch (Exception error) when (error is NativeApiException or AutomationException
                    or InvalidProtocolBufferException or InvalidJsonException)
                {
                    receipts.MarkUnknownHeld(instanceId, id, true, "operation_outcome_unknown", error.Message);
                    throw;
                }
            }
            string resultJson = SchematicJson.Formatter.Format(state);
            if (state.Status == PcbDrcJobStatus.PdrcjsStale)
                receipts.MarkStaleHeld(instanceId, id, client.Epoch, state.JobId, resultJson,
                    state.ErrorCode, state.ErrorMessage);
            else
                receipts.MarkCompletedHeld(instanceId, id, client.Epoch, state.JobId, resultJson);
            if (candidate is not null)
                CandidateReceipts[state.JobId] = new CandidateReceipt(
                    client.Epoch, document, expected.Clone(), candidateHash,
                    delete?.ItemIds.Select(item => item.Value).Order(StringComparer.Ordinal).ToArray() ?? []);
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
    }

    [McpServerTool(Name = "kicad_pcb_drc_start_typed"),
     Description("Typed PCB DRC start boundary. input carries the explicit PCB document, native document revision, process epoch, operation identity and DRC options; legacy JSON remains available through kicad_pcb_drc_start. Exactly one structured input form is admitted and the same native snapshot, candidate validation and receipt rules apply."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "typed PCB document, revision, process epoch and operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> StartTyped(string instanceId, PcbDrcStartInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            (string document, string revision) = input.ToPayload();
            return await Start(instanceId, document, input.OperationId!, input.RefillZones, input.ReportAllTrackErrors,
                input.TestFootprints, revision, input.ProcessEpoch!, cancellationToken,
                input.ExpectedSchematicStateJson, input.AllowDuplicateSheetNames, input.CandidateRequestJson,
                input.DeleteRequestJson, input.GroupChangesJson);
        }
        catch (AutomationException error)
        {
            return TypedError(error);
        }
    }

    [McpServerTool(Name = "kicad_pcb_drc_job_typed", ReadOnly = true),
     Description("Read one exact native PCB DRC job from a structured input. input carries the explicit PCB document, canonical lowercase job ID and native process epoch; optional detached-candidate fields preserve the exact protobuf payload used by a candidate start. The same stale-epoch, candidate receipt and complete snapshot rules as kicad_pcb_drc_job apply."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "typed PCB document, process epoch and job ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> JobTyped(string instanceId, PcbDrcJobInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = input.ToPayload();
            return await Job(instanceId, payload.Document, payload.JobId, payload.ProcessEpoch,
                cancellationToken, input.CandidateRequestJson, input.DeleteRequestJson, input.GroupChangesJson);
        }
        catch (AutomationException error)
        {
            return TypedError(error);
        }
    }

    [McpServerTool(Name = "kicad_pcb_drc_job", ReadOnly = true),
     Description("Read one exact native PCB DRC job by instance epoch, document and canonical lowercase job ID. Returned job, operation and revision identities are canonical lowercase UUIDs; malformed identity fails closed. Running jobs expose no findings. resultsFresh is true only for a completed job whose snapshot is complete (snapshotComplete) while none of its captured inputs has changed: every read compares them with the live ones before answering. Each finding names objects of the open board, the open drawing sheet, the job's own candidate items or, for copper KiCad rebuilt while refilling zones, the tuning pattern, via stitching or teardrop of the open board it stands for; a finding that can name none of them leaves the snapshot incomplete ('snapshot_incomplete: generated_item_identity'). Until the worker has finished, snapshotComplete is false with the reason 'snapshot_incomplete: finding_identity_pending'; a job that ends without findings (cancelled, failed, incomplete or stale) drops it. A job becomes stale, with no findings, once KiCad observes a change to the live board, its custom rules file, footprint libraries, project settings, the board's current variant, the current date or version-control revision its text shows, a font file its text uses, drawing sheet, router settings or parity schematic, by a PCB editor notification, window activation or the next read; a running job is then cancelled. Once stale, a job never becomes current again: start a new job. While a job runs, phase names the KiCad check step in progress and progress is the furthest fraction any single step has reached, not an overall percentage. A job ends completed (findings), cancelled, failed, incomplete or stale; only completed carries findings. A job whose custom rules file cannot be compiled, or declares a rules version newer than this KiCad reads, fails with design_rules_invalid; its message names the rules file and either the item that does not compile with its line and offset in that file, or that the file declares a rules version newer than the newest this KiCad reads, which it names: correct or remove the rules, then start a new job. Does not rerun or mutate the board."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "process epoch, document and job ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits", "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public async Task<CallToolResult> Job(string instanceId, string documentJson, string jobId,
        string processEpoch, CancellationToken cancellationToken, string? candidateRequestJson = null,
        string? deleteRequestJson = null, string? groupChangesJson = null)
    {
        PcbDrcJobState? state = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ParseDocument(documentJson);
            ValidatePcb(document);
            string id = Identifier(jobId, "job");
            var client = registry.Client(instanceId);
            if (processEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "The DRC job belongs to another native process epoch.");
            CreateItems? candidate = null;
            DeleteItems? delete = null;
            ReadPcbDrcJob read = new() { Document = document, JobId = id, ProcessEpoch = processEpoch };
            var groupChanges = ParseGroupChanges(groupChangesJson, "invalid_drc_group_changes");
            read.ExpectedGroupChanges.Add(groupChanges);
            if (!string.IsNullOrWhiteSpace(candidateRequestJson))
            {
                try { candidate = BoardJson.Parser.Parse<CreateItems>(candidateRequestJson); }
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
                    or InvalidOperationException)
                { throw new AutomationException("invalid_drc_candidate", "candidateRequestJson is not a valid board CreateItems request: " + error.Message); }
                ValidateCandidate(candidate, document, allowEmpty: true);
                read.ExpectedCandidateItems.Add(candidate.Items);
            }
            if (!string.IsNullOrWhiteSpace(deleteRequestJson))
            {
                try { delete = BoardJson.Parser.Parse<DeleteItems>(deleteRequestJson); }
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
                    or InvalidOperationException)
                { throw new AutomationException("invalid_drc_delete_candidate", "deleteRequestJson is not a valid board DeleteItems request: " + error.Message); }
                if (delete.Header?.Document is null || !delete.Header.Document.Equals(document)
                    || delete.ItemIds.Count == 0
                    || delete.ItemIds.Any(item => !CanonicalUuid(item.Value))
                    || delete.ItemIds.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count() != delete.ItemIds.Count)
                    throw new AutomationException("invalid_drc_delete_candidate", "deleteRequestJson must target this PCB with distinct canonical item IDs.");
                candidate ??= new CreateItems { Header = new() { Document = document } };
            }
            if (candidate is not null && candidate.Items.Count == 0 && delete is null)
                throw new AutomationException("invalid_drc_candidate", "An empty route candidate requires explicit removals.");
            if (candidate is not null)
            {
                var replacementRead = new NativeCapabilityPcbDrcReplacementJob
                {
                    Document = document, JobId = id, ProcessEpoch = processEpoch
                };
                replacementRead.ExpectedCandidateItems.Add(candidate.Items);
                if (delete is not null) replacementRead.ExpectedDeleteItemIds.Add(delete.ItemIds);
                replacementRead.ExpectedGroupChanges.Add(groupChanges);
                state = await client.InvokeAsync<NativeCapabilityPcbDrcReplacementJob, PcbDrcJobState>(replacementRead, cancellationToken);
            }
            else
            {
                state = await client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(read, cancellationToken);
            }
            ValidateJobState(state, document, processEpoch);
            if (state.JobId != id) throw new AutomationException("invalid_drc_job_state", "Native DRC job identity mismatch.");
            ForgetCandidateReceiptOnStale(state);
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
    }

    [McpServerTool(Name = "kicad_pcb_drc_operation", ReadOnly = true),
     Description("Look up one process-owned native PCB DRC job by its original canonical operation ID after a lost start reply or MCP reconnect. Supply an explicit PCB document and the current canonical process epoch. The lookup validates the response identity and normal PCB DRC job state, never starts or replays a job, and returns found=false with retrySafety=unknown-native-outcome when this native process has no authoritative receipt."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "process epoch, document and operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Operation(string instanceId, NativeDocumentInput document,
        string operationId, string processEpoch, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = OperationId(operationId);
            string epoch = ProcessEpoch(processEpoch);
            DocumentSpecifier target = NativeDocumentInput.ToNative(document);
            ValidatePcb(target);
            var client = registry.Client(instanceId);
            var session = await client.HandshakeAsync(cancellationToken);
            if (session.InstanceId != instanceId || session.Epoch != epoch)
                throw new AutomationException("stale_process_epoch", "The DRC operation belongs to another native process epoch.");
            var durable = receipts.Read(instanceId, id);
            if (durable is not null)
            {
                if (durable.ProcessEpoch != epoch)
                    throw new AutomationException("stale_process_epoch", "The durable DRC operation belongs to another native process epoch.");
                if (durable.DocumentJson != SchematicJson.Formatter.Format(target))
                    throw new AutomationException("drc_receipt_identity_mismatch", "The durable DRC operation belongs to another PCB document.");
            }

            NativeCapabilityPcbDrcReadOperationResponse native;
            try
            {
                native = await client.InvokeAsync<NativeCapabilityPcbDrcReadOperation,
                    NativeCapabilityPcbDrcReadOperationResponse>(new()
                    {
                        Document = target,
                        OperationId = id,
                        ProcessEpoch = epoch
                    }, cancellationToken);
            }
            catch (NativeApiException error) when (error.Status == (int)ApiStatusCode.AsUnhandled
                    || error.Status == (int)ApiStatusCode.AsUnimplemented)
            {
                throw new AutomationException("drc_operation_lookup_unavailable",
                    "The connected KiCad peer does not provide native PCB DRC operation lookup.");
            }

            if (!target.Equals(native.Document) || native.OperationId != id || native.ProcessEpoch != epoch)
                throw new AutomationException("invalid_drc_operation_result",
                    "Native DRC operation lookup returned a different document, operation or process epoch.");
            if (!native.Found && native.Job is not null)
                throw new AutomationException("invalid_drc_operation_result",
                    "Native DRC operation lookup reported found=false with a job state.");
            if (!native.Found)
            {
                if (durable is { Outcome: PcbDrcReceiptOutcome.Prepared or PcbDrcReceiptOutcome.Unknown })
                    return JsonSerializer.SerializeToElement(new
                    {
                        instanceId,
                        processEpoch = epoch,
                        document = target,
                        operationId = id,
                        found = false,
                        lookupSupported = true,
                        retrySafety = "unknown-native-outcome",
                        receiptScope = "mcp-state",
                        durableOutcome = durable.Outcome.ToString()
                    });
                return JsonSerializer.SerializeToElement(new
                {
                    instanceId,
                    processEpoch = epoch,
                    document = target,
                    operationId = id,
                    found = false,
                    lookupSupported = true,
                    retrySafety = "unknown-native-outcome"
                });
            }
            if (native.Job is null)
                throw new AutomationException("invalid_drc_operation_result",
                    "Native DRC operation lookup reported found=true without a job state.");
            ValidateJobState(native.Job, target, epoch);
            if (native.Job.OperationId != id)
                throw new AutomationException("invalid_drc_operation_result",
                    "Native DRC operation lookup returned a job for a different operation.");
            ForgetCandidateReceiptOnStale(native.Job);
            string nativeResultJson = SchematicJson.Formatter.Format(native.Job);
            if (durable is not null)
            {
                if (native.Job.Status == PcbDrcJobStatus.PdrcjsStale)
                    durable = receipts.MarkStale(instanceId, id, epoch, native.Job.JobId, nativeResultJson,
                        native.Job.ErrorCode, native.Job.ErrorMessage);
                else if (durable.CandidateJson.Length == 0 || native.Job.CandidatePayloadVerified)
                    durable = receipts.MarkCompleted(instanceId, id, epoch, native.Job.JobId, nativeResultJson);
                if (durable.CandidateJson.Length != 0 && native.Job.CandidatePayloadVerified)
                    RestoreCandidateReceipt(durable, native.Job);
            }
            using var jobJson = JsonDocument.Parse(nativeResultJson);
            return JsonSerializer.SerializeToElement(new
            {
                instanceId,
                processEpoch = epoch,
                document = target,
                operationId = id,
                found = true,
                lookupSupported = true,
                job = jobJson.RootElement.Clone(),
                receiptScope = durable is null ? "native-process" : "native-process-with-mcp-state",
                durableOutcome = durable?.Outcome.ToString()
            });
        });

    [McpServerTool(Name = "kicad_pcb_drc_operation_typed", ReadOnly = true),
     Description("Look up one exact native PCB DRC operation from a structured input. input carries the explicit PCB document, canonical lowercase operation ID and native process epoch; it never starts or replays a job and preserves unknown-native-outcome semantics after a lost reply."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "typed PCB document, process epoch and operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> OperationTyped(string instanceId, PcbDrcOperationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = input.ToPayload();
            return await Operation(instanceId, payload.Document, payload.OperationId, payload.ProcessEpoch,
                cancellationToken);
        }
        catch (AutomationException error)
        {
            return TypedError(error);
        }
    }

    [McpServerTool(Name = "kicad_pcb_drc_operation_receipt", ReadOnly = true),
     Description("Read the durable MCP-side admission receipt for one PCB DRC operation after an MCP reconnect. The receipt preserves the exact document, revision, candidate and original process-epoch binding, but never substitutes for KiCad's native job result. After a native restart, supply the current epoch to read the old receipt with durableProcessEpoch; an unknown or prepared record remains unknown and is never replayed automatically."),
     KiCadCapability("pcb", "durable-mcp-receipt", "typed PCB document, process epoch and operation ID"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "PcbDrcReceiptStoreTests.DurableReceiptSurvivesStoreRecreation")]
    public async Task<CallToolResult> OperationReceiptTyped(string instanceId,
        PcbDrcOperationReceiptInput input, CancellationToken cancellationToken)
    {
        try
        {
            var payload = input.ToPayload();
            return await OperationReceipt(instanceId, payload.Document, payload.OperationId,
                payload.ProcessEpoch, cancellationToken);
        }
        catch (AutomationException error)
        {
            return TypedError(error);
        }
    }

    private Task<CallToolResult> OperationReceipt(string instanceId, NativeDocumentInput document,
        string operationId, string processEpoch, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = OperationId(operationId);
            string epoch = ProcessEpoch(processEpoch);
            DocumentSpecifier target = NativeDocumentInput.ToNative(document);
            ValidatePcb(target);
            var client = registry.Client(instanceId);
            var session = await client.HandshakeAsync(cancellationToken);
            if (session.InstanceId != instanceId || session.Epoch != epoch)
                throw new AutomationException("stale_process_epoch", "The durable DRC receipt belongs to another native process epoch.");
            var durable = receipts.Read(instanceId, id);
            if (durable is null)
                return JsonSerializer.SerializeToElement(new
                {
                    instanceId, processEpoch = epoch, document = target, operationId = id,
                    found = false, receiptScope = "mcp-state", nativeReceiptAvailable = false
                });
            if (durable.DocumentJson != SchematicJson.Formatter.Format(target))
                throw new AutomationException("drc_receipt_identity_mismatch", "The durable DRC receipt belongs to another PCB document.");
            // A replacement native process has a new epoch, but the MCP-side
            // journal remains authoritative for the old admission. Return it
            // as receipt-only evidence and never dispatch a native lookup or
            // retry. The process-owned kicad_pcb_drc_operation boundary above
            // remains fail-closed on a stale epoch.
            if (durable.ProcessEpoch != epoch)
            {
                JsonElement? retainedAfterRestart = null;
                if (durable.ResultJson is not null)
                {
                    try
                    {
                        var retained = SchematicJson.Parser.Parse<PcbDrcJobState>(durable.ResultJson);
                        ValidateJobState(retained, target, durable.ProcessEpoch);
                        retainedAfterRestart = JsonSerializer.SerializeToElement(retained);
                    }
                    catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
                        or AutomationException)
                    { throw new AutomationException("drc_receipt_corrupt", "The retained PCB DRC result is invalid: " + error.Message); }
                }
                return JsonSerializer.SerializeToElement(new
                {
                    instanceId,
                    processEpoch = epoch,
                    durableProcessEpoch = durable.ProcessEpoch,
                    document = target,
                    operationId = id,
                    jobId = durable.JobId,
                    found = true,
                    receiptScope = "mcp-state",
                    nativeReceiptAvailable = false,
                    outcome = durable.Outcome.ToString(),
                    retrySafety = durable.Outcome == PcbDrcReceiptOutcome.Retryable
                        ? "safe-native-not-reached" : "unknown-native-outcome",
                    requestFingerprintSha256 = durable.RequestFingerprintSha256,
                    candidateHash = durable.CandidateHash,
                    result = retainedAfterRestart,
                    errorCode = durable.ErrorCode,
                    errorMessage = durable.ErrorMessage
                });
            }
            JsonElement? result = durable.ResultJson is null
                ? null : JsonDocument.Parse(durable.ResultJson).RootElement.Clone();
            if (durable.ResultJson is not null)
            {
                try
                {
                    var retained = SchematicJson.Parser.Parse<PcbDrcJobState>(durable.ResultJson);
                    ValidateJobState(retained, target, epoch);
                    RestoreCandidateReceipt(durable, retained);
                }
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
                    or AutomationException)
                { throw new AutomationException("drc_receipt_corrupt", "The retained PCB DRC result is invalid: " + error.Message); }
            }
            return JsonSerializer.SerializeToElement(new
            {
                instanceId,
                processEpoch = epoch,
                document = target,
                operationId = id,
                jobId = durable.JobId,
                found = true,
                receiptScope = "mcp-state",
                nativeReceiptAvailable = false,
                outcome = durable.Outcome.ToString(),
                retrySafety = durable.Outcome == PcbDrcReceiptOutcome.Retryable
                    ? "safe-native-not-reached" : "unknown-native-outcome",
                requestFingerprintSha256 = durable.RequestFingerprintSha256,
                candidateHash = durable.CandidateHash,
                result,
                errorCode = durable.ErrorCode,
                errorMessage = durable.ErrorMessage
            });
        });

    [McpServerTool(Name = "kicad_pcb_drc_cancel"),
     Description("Cancel one exact native PCB DRC job by process epoch, document and canonical lowercase job ID. Returns at once with cancellationRequested; the job reports cancelled only after its worker has stopped, and a cancelled job never carries findings or becomes completed. Read it with kicad_pcb_drc_job until workerFinished. A job that already finished keeps its terminal state."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "process epoch, document and job ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public async Task<CallToolResult> Cancel(string instanceId, string documentJson, string jobId,
        string processEpoch, CancellationToken cancellationToken)
    {
        PcbDrcJobState? state = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ParseDocument(documentJson);
            ValidatePcb(document);
            string id = Identifier(jobId, "job");
            var client = registry.Client(instanceId);
            if (processEpoch != client.Epoch)
                throw new AutomationException("stale_process_epoch", "The DRC job belongs to another native process epoch.");
            state = await client.InvokeAsync<CancelPcbDrcJob, PcbDrcJobState>(new()
                { Document = document, JobId = id, ProcessEpoch = processEpoch }, cancellationToken);
            ValidateJobState(state, document, processEpoch);
            if (state.JobId != id) throw new AutomationException("invalid_drc_job_state", "Native DRC job identity mismatch.");
            ForgetCandidateReceiptOnStale(state);
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
    }

    [McpServerTool(Name = "kicad_pcb_drc_cancel_typed"),
     Description("Cancel one exact native PCB DRC job from a structured input. input carries the explicit PCB document, canonical lowercase job ID and native process epoch; the returned state is the same cancellation acknowledgement used by kicad_pcb_drc_cancel."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "typed PCB document, process epoch and job ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> CancelTyped(string instanceId, PcbDrcCancelInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = input.ToPayload();
            return await Cancel(instanceId, payload.Document, payload.JobId, payload.ProcessEpoch,
                cancellationToken);
        }
        catch (AutomationException error)
        {
            return TypedError(error);
        }
    }

    [McpServerTool(Name = "kicad_pcb_drc_state", ReadOnly = true),
     Description("Read the selected PCB's real native DRC marker inventory, identities, exclusion flags and comments. Reports running calculation without traversing in-progress markers. This does not run DRC or prove that existing findings are fresh for the current design; freshness remains explicit."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "explicit instance ID and PCB document"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document;
            try { document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_document_target", "Provide a valid PCB document descriptor: " + error.Message); }
            DocumentStateTools.ValidateTarget(document);
            if ((int)document.Type != 3)
                throw new AutomationException("unsupported_document_type", "This marker query requires a PCB document.");
            var client = registry.Client(instanceId);
            var state = await client.InvokeAsync<ReadPcbDrcState, PcbDrcState>(new() { Document = document }, cancellationToken);
            if (!document.Equals(state.Document) || state.ProcessEpoch != client.Epoch
                || state.Revision is null || !CanonicalUuid(state.Revision.Epoch)
                || (state.Running && (state.MarkerSnapshotComplete || state.Findings.Count != 0))
                || (!state.Running && !state.MarkerSnapshotComplete)
                || state.Findings.Any(finding => !CanonicalUuid(finding.NativeId) || finding.Marker is null)
                || state.Findings.Select(finding => finding.NativeId).Distinct().Count() != state.Findings.Count)
                throw new AutomationException("invalid_drc_state", "Native marker inventory did not match the requested target or snapshot contract.");
            return SchematicJson.Formatter.Format(state);
        });

    [McpServerTool(Name = "kicad_pcb_drc_state_typed", ReadOnly = true),
     Description("Read the selected PCB's native DRC marker inventory from a structured input. input carries the explicit PCB document; the query is read-only and retains the freshness semantics of kicad_pcb_drc_state."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "typed PCB document"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> ReadTyped(string instanceId, PcbDrcStateInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Read(instanceId, input.ToPayload(), cancellationToken);
        }
        catch (AutomationException error)
        {
            return TypedError(error);
        }
    }

    // The prefix of the input warning that says why a job's snapshot is incomplete.
    internal const string IncompleteSnapshot = "snapshot_incomplete: ";

    private static DocumentSpecifier ParseDocument(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_document_target", "Provide a valid PCB document descriptor: " + error.Message); }
    }

    private static List<NativeRouteGroupChange> ParseGroupChanges(string? json, string code)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("groupChangesJson must be a JSON array.");
            var result = new List<NativeRouteGroupChange>();
            foreach (var element in document.RootElement.EnumerateArray())
                result.Add(SchematicJson.Parser.Parse<NativeRouteGroupChange>(element.GetRawText()));
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException(code, "groupChangesJson is not a valid typed grouped-route payload: " + error.Message); }
    }

    private static void ValidatePcb(DocumentSpecifier document)
    {
        DocumentStateTools.ValidateTarget(document);
        if ((int)document.Type != 3)
            throw new AutomationException("unsupported_document_type", "This DRC job requires a PCB document.");
    }

    private static string OperationId(string value) => Identifier(value, "operation");

    private static string ProcessEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty
            || !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw new AutomationException("invalid_process_epoch", "Provide the canonical native process epoch.");
        return id.ToString("D");
    }

    private static string Identifier(string value, string kind)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty
            || !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw new AutomationException("invalid_" + kind + "_id", $"Provide a nonempty {kind} UUID.");
        return id.ToString("D");
    }

    private static bool CanonicalUuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    // Native invalidation is deliberately a closed contract. A stale receipt
    // means that a captured dependency changed (or that its notification stream
    // was lost); arbitrary worker failures must remain failed/incomplete so a
    // caller can distinguish an obsolete result from a check that simply did not
    // finish.
    private static bool IsDependencyInvalidationCode(string? code) => code is
        "document_changed" or
        "project_inputs_changed" or
        "library_inputs_changed" or
        "auxiliary_inputs_changed" or
        "schematic_changed" or
        "input_events_lost";

    private static CallToolResult TypedError(AutomationException error)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = error.Code, errorMessage = error.Message });
        return new CallToolResult { IsError = true, StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static void ValidateJobState(PcbDrcJobState state, DocumentSpecifier document,
        string processEpoch)
    {
        if (state.CheckedSchematicState is not null)
            ValidateSchematicState(state.CheckedSchematicState, document, processEpoch);
        if (!document.Equals(state.Document) || state.ProcessEpoch != processEpoch
            || !CanonicalUuid(state.JobId)
            || !CanonicalUuid(state.OperationId)
            || state.CheckedRevision is null
            || !CanonicalUuid(state.CheckedRevision.Epoch)
            || (int)state.Status is < 1 or > 7
            || !double.IsFinite(state.Progress) || state.Progress < 0 || state.Progress > 1
            || (state.WorkerFinished != ((int)state.Status >= 3))
            || ((int)state.Status != 3 && state.Findings.Count != 0)
            || ((int)state.Status == 3 && state.Progress != 1)
            || (state.ResultsFresh && ((int)state.Status != 3 || !state.SnapshotComplete))
            // An unfinished check has not yet matched its findings to objects of the open board.
            || (!state.WorkerFinished && state.SnapshotComplete)
            // An incomplete snapshot always carries its reason, and a complete one none.
            || state.SnapshotComplete == state.InputWarnings.Any(warning => warning.StartsWith(IncompleteSnapshot, StringComparison.Ordinal))
            || (!state.CandidateDryRun && state.CandidateItemIds.Count != 0)
            || state.CandidateItemIds.Any(id => !CanonicalUuid(id))
            || (!state.GroupPayloadVerified && (state.VerifiedGroupChanges.Count != 0 || state.AfterGroupStates.Count != 0))
            || (state.GroupPayloadVerified && (state.VerifiedGroupChanges.Count == 0
                || state.AfterGroupStates.Count != state.VerifiedGroupChanges.Count))
            || ((int)state.Status == 4 && !state.CancellationRequested)
            || ((int)state.Status == 6 && !state.CancellationRequested)
            // A dependency invalidation requests cancellation, but a completed
            // result may never claim both cancellation and freshness. Once the
            // worker quiesces, the only terminal state for that request is STALE
            // with one of the native dependency reasons above.
            || (state.CancellationRequested && (state.ResultsFresh || (int)state.Status is 3 or 5 or 7))
            || ((int)state.Status == 6 && !IsDependencyInvalidationCode(state.ErrorCode))
            || ((int)state.Status is 5 or 6 or 7
                && (string.IsNullOrWhiteSpace(state.ErrorCode) || string.IsNullOrWhiteSpace(state.ErrorMessage)))
            || state.Findings.Any(finding => !CanonicalUuid(finding.NativeId)
            || finding.Marker is null)
            || state.Findings.Select(finding => finding.NativeId).Distinct().Count() != state.Findings.Count)
            throw new AutomationException("invalid_drc_job_state", "Native DRC job state did not match its target, identity or terminal-state contract.");
    }

    private static void ValidateCandidate(CreateItems candidate, DocumentSpecifier document, bool allowEmpty = false)
    {
        if (candidate.Header?.Document is null || !candidate.Header.Document.Equals(document)
            || candidate.Container is not null || candidate.Header.Container is not null || candidate.Header.FieldMask is not null
            || (!allowEmpty && candidate.Items.Count == 0))
            throw new AutomationException("invalid_drc_candidate", "A dry-run candidate must target the exact PCB document and contain at least one item.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packed in candidate.Items)
        {
            string id; string net; int layer = -1;
            if (packed.Is(Track.Descriptor))
            {
                var item = packed.Unpack<Track>(); id = item.Id.Value; net = item.Net.Name; layer = (int)item.Layer;
                if (item.Width.ValueNm <= 0) throw new AutomationException("invalid_drc_candidate", "Track width must be positive.");
            }
            else if (packed.Is(Arc.Descriptor))
            {
                var item = packed.Unpack<Arc>(); id = item.Id.Value; net = item.Net.Name; layer = (int)item.Layer;
                if (item.Width.ValueNm <= 0) throw new AutomationException("invalid_drc_candidate", "Arc width must be positive.");
            }
            else if (packed.Is(Via.Descriptor))
            {
                var item = packed.Unpack<Via>(); id = item.Id.Value; net = item.Net.Name;
                if (item.PadStack is null || item.PadStack.Layers.Count == 0)
                    throw new AutomationException("invalid_drc_candidate", "A via must declare its pad-stack layers.");
            }
            else throw new AutomationException("invalid_drc_candidate", "Dry-run candidates must contain only Track, Arc or Via items.");
            if (!CanonicalUuid(id) || !ids.Add(id))
                throw new AutomationException("invalid_drc_candidate", "Dry-run candidates need distinct canonical identities.");
            if (string.IsNullOrWhiteSpace(net))
                throw new AutomationException("invalid_drc_candidate", "Dry-run candidates need explicit existing-net names.");
            if (layer >= 0 && (layer < 3 || layer > 34))
                throw new AutomationException("invalid_drc_candidate", "Tracks and arcs must use copper layers.");
        }
    }

    // Candidate payloads are an MCP-process cache, not a durable native receipt.
    // Require the caller to bind a lookup to the currently attached native epoch
    // so a native restart cannot make an old candidate appear replayable.
    internal static bool TryGetCandidateReceipt(string jobId, string expectedProcessEpoch,
        out string processEpoch, out DocumentSpecifier document,
        out KiCad.Automation.Protocol.DocumentRevision revision,
        out string candidateHash, out IReadOnlyList<string> deleteItemIds)
    {
        if (CanonicalUuid(jobId) && CanonicalUuid(expectedProcessEpoch)
            && CandidateReceipts.TryGetValue(jobId, out CandidateReceipt? receipt)
            && string.Equals(receipt.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
        {
            processEpoch = receipt.ProcessEpoch; document = receipt.Document.Clone(); revision = receipt.Revision.Clone();
            candidateHash = receipt.CandidateHash; deleteItemIds = receipt.DeleteItemIds.ToArray(); return true;
        }
        processEpoch = ""; document = new(); revision = new(); candidateHash = ""; deleteItemIds = []; return false;
    }

    private void ForgetCandidateReceiptOnStale(PcbDrcJobState state)
    {
        if (state.Status == PcbDrcJobStatus.PdrcjsStale)
        {
            CandidateReceipts.TryRemove(state.JobId, out _);
            if (PcbDrcReceiptRecord.Canonical(state.OperationId))
            {
                foreach (var instance in registry.List())
                {
                    var durable = receipts.Read(instance.InstanceId, state.OperationId);
                    if (durable is null || durable.ProcessEpoch != state.ProcessEpoch || durable.JobId != state.JobId)
                        continue;
                    receipts.MarkStale(durable.InstanceId, state.OperationId, state.ProcessEpoch, state.JobId,
                        SchematicJson.Formatter.Format(state), state.ErrorCode, state.ErrorMessage);
                    break;
                }
            }
        }
    }

    private static string CanonicalGroupChanges(IReadOnlyList<NativeRouteGroupChange> changes) =>
        string.Join("\n", changes.Select(change => SchematicJson.Formatter.Format(change)));

    private static void RestoreCandidateReceipt(PcbDrcReceiptRecord receipt, PcbDrcJobState state)
    {
        if (receipt.CandidateJson.Length == 0 || receipt.JobId is null || !state.CandidateDryRun)
            return;
        try
        {
            var candidate = BoardJson.Parser.Parse<CreateItems>(receipt.CandidateJson);
            var delete = receipt.DeleteJson.Length == 0
                ? null : BoardJson.Parser.Parse<DeleteItems>(receipt.DeleteJson);
            CandidateReceipts[state.JobId] = new CandidateReceipt(
                receipt.ProcessEpoch, state.Document.Clone(), receipt.RevisionJson.Length == 0
                    ? new() : SchematicJson.Parser.Parse<Protocol.DocumentRevision>(receipt.RevisionJson),
                receipt.CandidateHash,
                delete?.ItemIds.Select(item => item.Value).Order(StringComparer.Ordinal).ToArray() ?? []);
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException)
        {
            throw new AutomationException("drc_receipt_corrupt", "The retained PCB DRC candidate is invalid: " + error.Message);
        }
    }

    internal static string CanonicalCandidate(CreateItems candidate)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BoardJson.Formatter.Format(candidate))));

    private sealed record CandidateReceipt(string ProcessEpoch, DocumentSpecifier Document,
        KiCad.Automation.Protocol.DocumentRevision Revision, string CandidateHash, IReadOnlyList<string> DeleteItemIds);

    private static void ValidateSchematicState(DocumentLifecycleState source, DocumentSpecifier board, string epoch)
    {
        if (source.Document is null || (int)source.Document.Type != 1
            || !Equals(source.Document.Project, board.Project)
            || source.Document.SheetPath is null || source.Document.SheetPath.Path.Count == 0
            || source.ProcessEpoch != epoch || (int)source.Scope != 1 || !source.ProjectSettingsIncluded
            || source.Revision is null || source.StateSha256.Length != 64
            || !source.StateSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_schematic_state", "Provide the observed schematic state from this project and native process.");
        DocumentStateTools.ValidateTarget(source.Document);
        _ = Identifier(source.NativeIdentity, "schematic");
        _ = Identifier(source.Revision.Epoch, "schematic_epoch");
        foreach (var sheet in source.Document.SheetPath.Path) _ = Identifier(sheet.Value, "sheet");
    }

    private static CallToolResult WithStructuredState(CallToolResult response, PcbDrcJobState? state)
    {
        if (state is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(state));
            response.StructuredContent = json.RootElement.Clone();
        }
        return response;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbDrcStartInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private NativeDocumentRevisionInput? revision;
    private string? operationId;
    private string? processEpoch;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public NativeDocumentRevisionInput? Revision { get => revision; set => revision = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
    public bool RefillZones { get; set; }
    public bool ReportAllTrackErrors { get; set; }
    public bool TestFootprints { get; set; }
    public bool AllowDuplicateSheetNames { get; set; }
    public string? ExpectedSchematicStateJson { get; set; }
    public string? CandidateRequestJson { get; set; }
    public string? DeleteRequestJson { get; set; }
    public string? GroupChangesJson { get; set; }

    internal (string Document, string Revision) ToPayload()
    {
        if (Document is null || Revision is null || string.IsNullOrWhiteSpace(OperationId)
            || string.IsNullOrWhiteSpace(ProcessEpoch))
            throw new AutomationException("invalid_pcb_drc_typed_input", "Document, revision, operationId and processEpoch are required.");
        DocumentSpecifier target = NativeDocumentInput.ToNative(Document);
        if (target.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_drc_typed_input", "Document must identify a PCB.");
        if (!Guid.TryParseExact(OperationId, "D", out Guid operation) || operation == Guid.Empty
            || !string.Equals(OperationId, operation.ToString("D"), StringComparison.Ordinal)
            || !Guid.TryParseExact(ProcessEpoch, "D", out Guid epoch) || epoch == Guid.Empty
            || !string.Equals(ProcessEpoch, epoch.ToString("D"), StringComparison.Ordinal))
            throw new AutomationException("invalid_pcb_drc_typed_input", "operationId and processEpoch must be canonical lowercase UUIDs.");
        if (string.IsNullOrWhiteSpace(Revision.Epoch)
            || !Guid.TryParseExact(Revision.Epoch, "D", out Guid revisionEpoch) || revisionEpoch == Guid.Empty
            || !string.Equals(Revision.Epoch, revisionEpoch.ToString("D"), StringComparison.Ordinal))
            throw new AutomationException("invalid_pcb_drc_typed_input", "revision.epoch must be a canonical lowercase UUID.");
        return (SchematicJson.Formatter.Format(target), SchematicJson.Formatter.Format(new KiCad.Automation.Protocol.DocumentRevision
        { Epoch = Revision.Epoch, Sequence = Revision.Sequence }));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbDrcJobInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? jobId;
    private string? processEpoch;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? JobId { get => jobId; set => jobId = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }

    // Candidate payloads remain optional compatibility fields for jobs started
    // through the legacy protobuf-JSON candidate boundary.  The document and
    // lifecycle identities above are always structured and validated here.
    public string? CandidateRequestJson { get; set; }
    public string? DeleteRequestJson { get; set; }
    public string? GroupChangesJson { get; set; }

    internal (string Document, string JobId, string ProcessEpoch) ToPayload()
    {
        DocumentSpecifier target = PcbDrcTypedInputValidation.Document(Document, "invalid_pcb_drc_typed_job");
        return (SchematicJson.Formatter.Format(target),
            PcbDrcTypedInputValidation.Identifier(JobId, "job", "invalid_pcb_drc_typed_job"),
            PcbDrcTypedInputValidation.Identifier(ProcessEpoch, "process epoch", "invalid_pcb_drc_typed_job"));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbDrcOperationInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? operationId;
    private string? processEpoch;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }

    internal (NativeDocumentInput Document, string OperationId, string ProcessEpoch) ToPayload()
    {
        _ = PcbDrcTypedInputValidation.Document(Document, "invalid_pcb_drc_typed_operation");
        return (Document!,
            PcbDrcTypedInputValidation.Identifier(OperationId, "operation", "invalid_pcb_drc_typed_operation"),
            PcbDrcTypedInputValidation.Identifier(ProcessEpoch, "process epoch", "invalid_pcb_drc_typed_operation"));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbDrcOperationReceiptInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? operationId;
    private string? processEpoch;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }

    internal (NativeDocumentInput Document, string OperationId, string ProcessEpoch) ToPayload()
    {
        _ = PcbDrcTypedInputValidation.Document(Document, "invalid_pcb_drc_typed_receipt");
        return (Document!,
            PcbDrcTypedInputValidation.Identifier(OperationId, "operation", "invalid_pcb_drc_typed_receipt"),
            PcbDrcTypedInputValidation.Identifier(ProcessEpoch, "process epoch", "invalid_pcb_drc_typed_receipt"));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbDrcCancelInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? jobId;
    private string? processEpoch;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? JobId { get => jobId; set => jobId = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }

    internal (string Document, string JobId, string ProcessEpoch) ToPayload()
    {
        DocumentSpecifier target = PcbDrcTypedInputValidation.Document(Document, "invalid_pcb_drc_typed_cancel");
        return (SchematicJson.Formatter.Format(target),
            PcbDrcTypedInputValidation.Identifier(JobId, "job", "invalid_pcb_drc_typed_cancel"),
            PcbDrcTypedInputValidation.Identifier(ProcessEpoch, "process epoch", "invalid_pcb_drc_typed_cancel"));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbDrcStateInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }

    internal string ToPayload()
        => SchematicJson.Formatter.Format(PcbDrcTypedInputValidation.Document(Document, "invalid_pcb_drc_typed_state"));
}

internal static class PcbDrcTypedInputValidation
{
    internal static DocumentSpecifier Document(NativeDocumentInput? input, string code)
    {
        if (input is null)
            throw new AutomationException(code, "Document is required.");
        DocumentSpecifier target;
        try { target = NativeDocumentInput.ToNative(input); }
        catch (AutomationException error)
        { throw new AutomationException(code, error.Message); }
        if (target.Type != DocumentType.DoctypePcb)
            throw new AutomationException(code, "Document must identify a PCB.");
        return target;
    }

    internal static string Identifier(string? value, string name, string code)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Guid.TryParseExact(value, "D", out Guid id)
            || id == Guid.Empty
            || !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw new AutomationException(code, $"{name} must be a canonical lowercase UUID.");
        return value;
    }
}
