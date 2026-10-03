using System.ComponentModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class SimulationTools(InstanceRegistry instances)
{
    private static readonly ConcurrentDictionary<string, DeckProvenance> Decks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SimulationOperation> Operations = new(StringComparer.Ordinal);
    [McpServerTool(Name = "kicad_simulation_start"),
     Description("Start one explicit ngspice simulation through KiCad's existing native simulator for a verified schematic document. Pass an empty netlist to simulate the deck KiCad generates from the schematic. A supplied netlist must be a plain self-contained circuit: interpreter blocks (.control/.exec, *ng_script) and file includes (.include, .lib file) are rejected. The MCP result records the inline deck SHA-256 when supplied and binds completed job reads to self-contained provenance. The native editor owns the simulator and its result vectors. Retry the same operation ID after a lost reply; a different operation cannot run concurrently."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, operation ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Start(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        string netlist, Guid operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        SimulationDeckAdmission.Validate(netlist);
        ValidateId(operationId, "operation");
        var target = SimulationDocumentInput.ToNative(document);
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var request = new StartSimulationJob
        {
            Document = target, OperationId = operationId.ToString("D"), ProcessEpoch = session.Epoch, Netlist = netlist
        };
        string operationKey = Key(instanceId, session.Epoch, request.OperationId);
        string? deckHash = string.IsNullOrEmpty(netlist) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(netlist)));
        var operation = new SimulationOperation(request.OperationId, target.Clone(), deckHash, string.IsNullOrEmpty(netlist) ? "native-schematic-generated" : "inline-caller-deck");
        if (Operations.TryGetValue(operationKey, out var prior)
            && (!prior.Document.Equals(target) || prior.Sha256 != operation.Sha256 || prior.Source != operation.Source))
            throw new AutomationException("simulation_operation_reused", "The operation ID is already bound to different schematic or deck bytes.");
        Operations[operationKey] = operation;

        var result = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(request, cancellationToken);
        SimulationJobContract.ValidateStartIdentity(request, result, session.Epoch);
        if (result.Status is SimulationJobStatus.SimjsCompleted or SimulationJobStatus.SimjsCancelled or SimulationJobStatus.SimjsFailed)
        {
            // A terminal Start replay may omit vectors and worker completion. Read
            // the same retained job instead of weakening the result contract.
            var retained = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = target, JobId = result.JobId, ProcessEpoch = session.Epoch }, cancellationToken);
            SimulationJobContract.ValidateRead(retained, target, session.Epoch, result.JobId, request.OperationId);
            if (retained.Status != result.Status)
                throw new AutomationException("invalid_simulation_result", "The terminal simulation state changed while reconciling the start reply.");
            result = retained;
        }
        SimulationJobContract.ValidateStart(request, result, session.Epoch);
        string deckKey = Key(instanceId, session.Epoch, result.JobId);
        Decks[deckKey] = new(session.Epoch, target, deckHash, operation.Source,
            SelfContained: !string.IsNullOrEmpty(netlist), DependenciesKnown: !string.IsNullOrEmpty(netlist), request.OperationId);
        if (Decks.Count > 4096)
        {
            string? oldest = Decks.Keys.FirstOrDefault();
            if (oldest is not null) Decks.TryRemove(oldest, out _);
        }
        if (Operations.Count > 4096 && Operations.Keys.FirstOrDefault() is { } oldOperation)
            Operations.TryRemove(oldOperation, out _);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(instanceId, session.Epoch, result.JobId), job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_job", ReadOnly = true),
     Description("Read one exact native KiCad simulation job by process epoch and job ID. Only a completed job exposes copied result vectors; running, cancelled and failed states remain explicit."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, job ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public Task<CallToolResult> Job(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        Guid jobId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateId(jobId, "job");
        var target = SimulationDocumentInput.ToNative(document);
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
        { Document = target, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch }, cancellationToken);
        string operationKey = Key(instanceId, session.Epoch, result.OperationId);
        Operations.TryGetValue(operationKey, out var operation);
        SimulationJobContract.ValidateRead(result, target, session.Epoch, jobId.ToString("D"), operation?.OperationId);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(instanceId, session.Epoch, result.JobId), job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_dependencies", ReadOnly = true),
     Description("Read the dependency evidence for one exact native KiCad simulation job. Pass the complete native document descriptor as protobuf JSON in documentJson, including its exact sheet-instance path. The response is bound to the process epoch, document, job, and original operation. A false dependencyCaptureComplete value is returned when KiCad or the retained deck has not enumerated external dependency paths; warnings remain explicit and are never converted into an empty successful dependency set."),
     KiCadCapability("simulation", "dependency-evidence", "instance epoch, document, job ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public Task<CallToolResult> Dependencies(string instanceId, string expectedInstanceEpoch, string documentJson,
        Guid jobId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateId(jobId, "job");
        DocumentSpecifier document;
        try { document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson); }
        catch (Exception error) when (error is Google.Protobuf.InvalidProtocolBufferException or Google.Protobuf.InvalidJsonException)
        {
            throw new AutomationException("invalid_document_target",
                "Provide a valid native document descriptor: " + error.Message);
        }
        DocumentStateTools.ValidateTarget(document);
        if ((int)document.Type != 1)
            throw new AutomationException("unsupported_simulation_document", "Simulation requires a schematic document.");
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await client.InvokeAsync<NativeCapabilitySimulationReadDependencies,
            NativeCapabilitySimulationDependencies>(new()
            {
                Document = document,
                JobId = jobId.ToString("D"),
                ProcessEpoch = session.Epoch
            }, cancellationToken);
        if (!result.Document.Equals(document)
            || result.JobId != jobId.ToString("D")
            || result.ProcessEpoch != session.Epoch)
            throw new AutomationException("simulation_dependency_identity_mismatch",
                "Native dependency evidence did not preserve the requested document, job, or process identity.");
        return Data(new
        {
            instanceId,
            instanceEpoch = session.Epoch,
            document,
            jobId = result.JobId,
            operationId = result.OperationId,
            generatedDeckSha256 = result.GeneratedDeckSha256,
            dependencyCaptureComplete = result.DependencyCaptureComplete,
            dependencyPaths = result.DependencyPaths.ToArray(),
            warnings = result.Warnings.ToArray()
        });
    });

    [McpServerTool(Name = "kicad_simulation_wait", ReadOnly = true),
     Description("Wait for one exact native simulation job to change state. This targets only the simulator job, not the design; cancellation or timeout never becomes a successful result."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, job ID, sequence"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public async Task<CallToolResult> Wait(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        Guid jobId, ulong afterSequence, CancellationToken cancellationToken)
    {
        try
        {
            ValidateId(jobId, "job");
            var target = SimulationDocumentInput.ToNative(document);
            var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
            if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
                throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var result = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
                { Document = target, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch }, deadline.Token);
                string operationKey = Key(instanceId, session.Epoch, result.OperationId);
                Operations.TryGetValue(operationKey, out var operation);
                SimulationJobContract.ValidateRead(result, target, session.Epoch, jobId.ToString("D"), operation?.OperationId);
                if (result.Sequence > afterSequence || result.Status is SimulationJobStatus.SimjsCompleted or SimulationJobStatus.SimjsCancelled or SimulationJobStatus.SimjsFailed)
                    return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(instanceId, session.Epoch, result.JobId), job = Describe(result) });
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or OperationCanceledException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "simulation_wait_incomplete", errorMessage = error.Message }, true); }
    }

    [McpServerTool(Name = "kicad_simulation_cancel"),
     Description("Cancel one exact native KiCad simulation job. The native simulator is stopped before the terminal Cancelled state is returned; result vectors from a cancelled run are never reported as successful output."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, job ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public Task<CallToolResult> Cancel(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        Guid jobId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateId(jobId, "job");
        var target = SimulationDocumentInput.ToNative(document);
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await client.InvokeAsync<CancelSimulationJob, SimulationJobState>(new()
        { Document = target, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch }, cancellationToken);
        string operationKey = Key(instanceId, session.Epoch, result.OperationId);
        Operations.TryGetValue(operationKey, out var operation);
        SimulationJobContract.ValidateRead(result, target, session.Epoch, jobId.ToString("D"), operation?.OperationId);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(instanceId, session.Epoch, result.JobId), job = Describe(result) });
    });

    private static object Describe(SimulationJobState value) => new
    {
        jobId = value.JobId, operationId = value.OperationId, status = value.Status.ToString(), sequence = value.Sequence,
        progress = value.Progress, cancellationRequested = value.CancellationRequested, workerFinished = value.WorkerFinished,
        errorCode = value.ErrorCode, errorMessage = value.ErrorMessage, messages = value.Messages,
        vectors = value.Status == SimulationJobStatus.SimjsCompleted ? value.Vectors.Select(v => new { name = v.Name, complex = v.Complex, values = v.Values }).ToArray() : []
    };

    private static object? DescribeDeck(string instanceId, string processEpoch, string jobId)
        => Decks.TryGetValue(Key(instanceId, processEpoch, jobId), out DeckProvenance? deck)
            ? new { source = deck.Source, sha256 = deck.Sha256, externalFiles = Array.Empty<string>(),
                selfContained = deck.SelfContained, dependenciesKnown = deck.DependenciesKnown,
                operationId = deck.OperationId }
            : null;

    private static string Key(string instanceId, string processEpoch, string id) => instanceId + "\n" + processEpoch + "\n" + id;

    private static void ValidateId(Guid id, string kind)
    {
        if (id == Guid.Empty)
            throw new AutomationException("invalid_simulation_request", "A nonempty simulation " + kind + " ID is required.");
    }

    private sealed record DeckProvenance(string ProcessEpoch, DocumentSpecifier Document, string? Sha256,
        string Source, bool SelfContained, bool DependenciesKnown, string OperationId);

    private sealed record SimulationOperation(string OperationId, DocumentSpecifier Document, string? Sha256, string Source);

    private static CallToolResult Data(object value, bool error = false)
    {
        var data = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "simulation_unavailable", errorMessage = error.Message }, true); }
    }
}
