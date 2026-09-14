using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
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
    // Deliberately not in tools/list until complete snapshot/isolation and real
    // cancellation journeys qualify this family (p23deb822a36256a6).
    public async Task<CallToolResult> Start(string instanceId, string documentJson, string operationId,
        bool refillZones, bool reportAllTrackErrors, bool testFootprints, string expectedRevisionJson,
        string processEpoch, CancellationToken cancellationToken)
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
            state = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(new()
            {
                Document = document,
                OperationId = id,
                RefillZones = refillZones,
                ReportAllTrackErrors = reportAllTrackErrors,
                TestFootprints = testFootprints,
                ExpectedRevision = expected,
                ProcessEpoch = processEpoch
            }, cancellationToken);
            ValidateJobState(state, document, client.Epoch);
            if (state.OperationId != id || !expected.Equals(state.CheckedRevision))
                throw new AutomationException("invalid_drc_job_state", "Native DRC admission does not match the requested operation and revision.");
            return SchematicJson.Formatter.Format(state);
        });
        return WithStructuredState(response, state);
    }

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
     Description("Read the selected PCB's real native DRC marker inventory, identities, exclusion flags and comments. Reports running calculation without traversing in-progress markers. This does not run DRC or prove that existing findings are fresh for the current design; freshness remains explicit.")]
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
            || ((int)state.Status == 4 && !state.CancellationRequested)
            || state.Findings.Any(finding => !Guid.TryParseExact(finding.NativeId, "D", out _)
            || finding.Marker is null)
            || state.Findings.Select(finding => finding.NativeId).Distinct().Count() != state.Findings.Count)
            throw new AutomationException("invalid_drc_job_state", "Native DRC job state did not match its target, identity or terminal-state contract.");
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
