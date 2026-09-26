using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Board.Types;
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
    [McpServerTool(Name = "kicad_pcb_drc_start"),
     Description("Start a revision-guarded native PCB DRC job for an explicit PCB document. Requires process epoch, document revision and operation identity; optional schematic parity checks require the exact observed schematic state. candidateRequestJson may contain explicit Track/Arc/Via items for a detached dry-run qualification; those candidates are never added to the live board. KiCad captures, at the requested revision, every input the enabled tests read: the board with the identity of each of its objects (teardrops included), the project with its design, net-class and text-variable settings, the board's current variant, the custom rules file, the footprint libraries, the drawing sheet, the router settings, exclusions with their comments, the current date or version-control revision when the board's text shows it (also the day an @{...} expression reads with today()), the content of each font file the board's text is laid out with (fonts embedded in the board are part of it) and, with testFootprints, the observed schematic. snapshotComplete is true only when all of them were captured exactly; otherwise inputWarnings says why in an entry that starts with 'snapshot_incomplete: '. A board whose text shows the time of day, which changes while the check runs, is never complete ('snapshot_incomplete: current_time_text'), and neither is one whose text or drawing sheet evaluates an @{...} expression that reads the time, chance or the version-control repository, such as now(), random() or vcsbranch(), or calls a function KiCad does not evaluate from the design alone ('snapshot_incomplete: volatile_text_expression'); a font file the check cannot read gives 'snapshot_incomplete: font_file'. Until a job's worker has finished, snapshotComplete is false with 'snapshot_incomplete: finding_identity_pending', because only a finished check has matched each finding to an object of the open board. Ordinary and candidate jobs follow the same rule. Returns a job snapshot and does not claim completion until kicad_pcb_drc_job reports a terminal result."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "process epoch, document revision, operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public async Task<CallToolResult> Start(string instanceId, string documentJson, string operationId,
        bool refillZones, bool reportAllTrackErrors, bool testFootprints, string expectedRevisionJson,
        string processEpoch, CancellationToken cancellationToken, string? expectedSchematicStateJson = null,
        bool allowDuplicateSheetNames = false, string? candidateRequestJson = null)
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
                catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
                { throw new AutomationException("invalid_drc_candidate", "candidateRequestJson is not a valid board CreateItems request: " + error.Message); }
                ValidateCandidate(candidate, document);
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
            state = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(request, cancellationToken);
            ValidateJobState(state, document, client.Epoch);
            if (state.OperationId != id || !expected.Equals(state.CheckedRevision))
                throw new AutomationException("invalid_drc_job_state", "Native DRC admission does not match the requested operation and revision.");
            if (!Equals(schematic, state.CheckedSchematicState))
                throw new AutomationException("invalid_drc_job_state", "Native DRC admission does not match the requested source schematic.");
            if (state.CandidateDryRun != (candidate is not null)
                || state.CandidateItemIds.Count != (candidate?.Items.Count ?? 0))
                throw new AutomationException("invalid_drc_job_state", "Native DRC did not preserve the detached candidate identity contract.");
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
    }

    [McpServerTool(Name = "kicad_pcb_drc_job", ReadOnly = true),
     Description("Read one exact native PCB DRC job by instance epoch, document and job ID. Running jobs expose no findings. resultsFresh is true only for a completed job whose snapshot is complete (snapshotComplete) while none of its captured inputs has changed: every read compares them with the live ones before answering. Each finding names objects of the open board, the open drawing sheet, the job's own candidate items or, for copper KiCad rebuilt while refilling zones, the tuning pattern, via stitching or teardrop of the open board it stands for; a finding that can name none of them leaves the snapshot incomplete ('snapshot_incomplete: generated_item_identity'). Until the worker has finished, snapshotComplete is false with the reason 'snapshot_incomplete: finding_identity_pending'; a job that ends without findings (cancelled, failed, incomplete or stale) drops it. A job becomes stale, with no findings, once KiCad observes a change to the live board, its custom rules file, footprint libraries, project settings, the board's current variant, the current date or version-control revision its text shows, a font file its text uses, drawing sheet, router settings or parity schematic, by a PCB editor notification, window activation or the next read; a running job is then cancelled. Once stale, a job never becomes current again: start a new job. While a job runs, phase names the KiCad check step in progress and progress is the furthest fraction any single step has reached, not an overall percentage. A job ends completed (findings), cancelled, failed, incomplete or stale; only completed carries findings. A job whose custom rules file cannot be compiled, or declares a rules version newer than this KiCad reads, fails with design_rules_invalid; its message names the rules file and either the item that does not compile with its line and offset in that file, or that the file declares a rules version newer than the newest this KiCad reads, which it names: correct or remove the rules, then start a new job. Does not rerun or mutate the board."),
     KiCadCapability("pcb", "compiled-mcp plus native-api", "process epoch, document and job ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits", "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public async Task<CallToolResult> Job(string instanceId, string documentJson, string jobId,
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
            state = await client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(new()
                { Document = document, JobId = id, ProcessEpoch = processEpoch }, cancellationToken);
            ValidateJobState(state, document, processEpoch);
            if (state.JobId != id) throw new AutomationException("invalid_drc_job_state", "Native DRC job identity mismatch.");
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
    }

    [McpServerTool(Name = "kicad_pcb_drc_cancel"),
     Description("Cancel one exact native PCB DRC job by process epoch, document and job ID. Returns at once with cancellationRequested; the job reports cancelled only after its worker has stopped, and a cancelled job never carries findings or becomes completed. Read it with kicad_pcb_drc_job until workerFinished. A job that already finished keeps its terminal state."),
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
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
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
                || state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out _)
                || (state.Running && (state.MarkerSnapshotComplete || state.Findings.Count != 0))
                || (!state.Running && !state.MarkerSnapshotComplete)
                || state.Findings.Any(finding => !Guid.TryParseExact(finding.NativeId, "D", out _) || finding.Marker is null)
                || state.Findings.Select(finding => finding.NativeId).Distinct().Count() != state.Findings.Count)
                throw new AutomationException("invalid_drc_state", "Native marker inventory did not match the requested target or snapshot contract.");
            return SchematicJson.Formatter.Format(state);
        });

    // The prefix of the input warning that says why a job's snapshot is incomplete.
    internal const string IncompleteSnapshot = "snapshot_incomplete: ";

    private static DocumentSpecifier ParseDocument(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_document_target", "Provide a valid PCB document descriptor: " + error.Message); }
    }

    private static void ValidatePcb(DocumentSpecifier document)
    {
        DocumentStateTools.ValidateTarget(document);
        if ((int)document.Type != 3)
            throw new AutomationException("unsupported_document_type", "This DRC job requires a PCB document.");
    }

    private static string OperationId(string value) => Identifier(value, "operation");

    private static string Identifier(string value, string kind)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_" + kind + "_id", $"Provide a nonempty {kind} UUID.");
        return id.ToString("D");
    }

    private static void ValidateJobState(PcbDrcJobState state, DocumentSpecifier document,
        string processEpoch)
    {
        if (state.CheckedSchematicState is not null)
            ValidateSchematicState(state.CheckedSchematicState, document, processEpoch);
        if (!document.Equals(state.Document) || state.ProcessEpoch != processEpoch
            || !Guid.TryParseExact(state.JobId, "D", out _)
            || state.JobId == Guid.Empty.ToString("D")
            || !Guid.TryParseExact(state.OperationId, "D", out _)
            || state.OperationId == Guid.Empty.ToString("D")
            || state.CheckedRevision is null
            || !Guid.TryParseExact(state.CheckedRevision.Epoch, "D", out _)
            || state.CheckedRevision.Epoch == Guid.Empty.ToString("D")
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
            || state.CandidateItemIds.Any(id => !Guid.TryParseExact(id, "D", out _) || id == Guid.Empty.ToString("D"))
            || ((int)state.Status == 4 && !state.CancellationRequested)
            || state.Findings.Any(finding => !Guid.TryParseExact(finding.NativeId, "D", out _)
            || finding.Marker is null)
            || state.Findings.Select(finding => finding.NativeId).Distinct().Count() != state.Findings.Count)
            throw new AutomationException("invalid_drc_job_state", "Native DRC job state did not match its target, identity or terminal-state contract.");
    }

    private static void ValidateCandidate(CreateItems candidate, DocumentSpecifier document)
    {
        if (candidate.Header?.Document is null || !candidate.Header.Document.Equals(document) || candidate.Items.Count == 0)
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
            if (!Guid.TryParseExact(id, "D", out var identity) || identity == Guid.Empty || !ids.Add(id))
                throw new AutomationException("invalid_drc_candidate", "Dry-run candidates need distinct canonical identities.");
            if (string.IsNullOrWhiteSpace(net))
                throw new AutomationException("invalid_drc_candidate", "Dry-run candidates need explicit existing-net names.");
            if (layer >= 0 && (layer < 3 || layer > 34))
                throw new AutomationException("invalid_drc_candidate", "Tracks and arcs must use copper layers.");
        }
    }

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
