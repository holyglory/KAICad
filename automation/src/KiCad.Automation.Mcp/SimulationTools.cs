using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed partial class SimulationTools(InstanceRegistry instances)
{
    private readonly SimulationResultReceipts receipts = new(instances.StateDirectory);
    [McpServerTool(Name = "kicad_simulation_start"),
     Description("Start one explicit ngspice simulation through KiCad's existing native simulator for a verified schematic document. Pass an empty netlist to simulate the deck KiCad generates from the schematic. A supplied netlist must be a plain self-contained circuit: interpreter blocks (.control/.exec, *ng_script) and file includes (.include, .lib file) are rejected. The MCP result records the inline deck SHA-256. An identical operation with a durable terminal receipt replays its archive without running again. A dispatched or accepted operation is recovered by its native operation identity when possible; if the native instance is unavailable or no authoritative native record exists it remains explicitly unresolved and is never automatically dispatched again. A different operation cannot replace an unarchived native job."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, operation ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Start(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        string netlist, Guid operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        SimulationDeckAdmission.Validate(netlist);
        ValidateId(operationId, "operation");
        var target = SimulationDocumentInput.ToNative(document);
        string? deckHash = string.IsNullOrEmpty(netlist) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(netlist)));
        var operation = SimulationOperationReceipt.Prepare(instanceId, expectedInstanceEpoch, target, operationId,
            deckHash, string.IsNullOrEmpty(netlist) ? "native-schematic-generated" : "inline-caller-deck");
        using var ownership = receipts.AcquireSession(instanceId);
        var previous = receipts.ReadOperation(instanceId, operationId);
        if (previous is not null)
        {
            if (!SimulationResultReceipts.SameFingerprint(previous, operation))
                throw new AutomationException("simulation_operation_reused", "The operation ID is already bound to different original instance, epoch, schematic or deck bytes.");
            if (receipts.ReadTerminal(previous) is { } archive)
                return ArchivedData(archive, target);
        }
        NativeClient client;
        try { client = instances.Client(instanceId); }
        catch (AutomationException error) when (previous?.Stage is SimulationOperationStage.Dispatched or SimulationOperationStage.Accepted
            && error.Code == "unknown_instance")
        { throw SimulationResultReceipts.Unresolved(previous.OperationId); }
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        if (previous?.Stage == SimulationOperationStage.Dispatched)
        {
            var recovered = await LookupNative(client, target, session.Epoch, previous.OperationId, cancellationToken);
            if (recovered is null)
                throw SimulationResultReceipts.Unresolved(previous.OperationId);
            receipts.WriteOperation(previous with { Stage = SimulationOperationStage.Accepted, JobId = recovered.JobId });
            if (Terminal(recovered))
                await CaptureTerminal(client, instanceId, target, session.Epoch, recovered, cancellationToken);
            return Data(new { instanceId, instanceEpoch = session.Epoch, document = target,
                deck = DescribeDeck(previous), job = Describe(recovered) });
        }
        if (previous?.Stage == SimulationOperationStage.Accepted)
        {
            var retained = await ReadNative(client, instanceId, session.Epoch, target, previous.JobId!, cancellationToken);
            return Data(new { instanceId, instanceEpoch = session.Epoch, document = target,
                deck = DescribeDeck(previous), job = Describe(retained) });
        }
        await PreserveCurrent(client, instanceId, session.Epoch, operation.OperationId, cancellationToken);
        receipts.WriteOperation(operation);
        receipts.MakeCurrent(operation);
        receipts.WriteOperation(operation = operation with { Stage = SimulationOperationStage.Dispatched });
        var request = new StartSimulationJob
        {
            Document = target, OperationId = operationId.ToString("D"), ProcessEpoch = session.Epoch, Netlist = netlist
        };
        // The dispatch marker is durable before native invocation. A lost reply
        // cannot erase the fingerprint or authorize another execution.
        var result = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(request, cancellationToken);
        SimulationJobContract.ValidateStartIdentity(request, result, session.Epoch);
        receipts.WriteOperation(operation = operation with { Stage = SimulationOperationStage.Accepted, JobId = result.JobId });
        if (Terminal(result))
        {
            // A terminal Start replay may omit vectors and worker completion. Read
            // the same retained job instead of weakening the result contract.
            var retained = await ReadNative(client, instanceId, session.Epoch, target, result.JobId, cancellationToken);
            if (retained.Status != result.Status)
                throw new AutomationException("invalid_simulation_result", "The terminal simulation state changed while reconciling the start reply.");
            result = retained;
        }
        SimulationJobContract.ValidateStart(request, result, session.Epoch);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(operation), job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_job", ReadOnly = true),
     Description("Read one exact native KiCad simulation job by process epoch and job ID. Only a completed job exposes copied result vectors; running, cancelled and failed states remain explicit. Real vectors have one value per sample; vectors with complex=true contain ordered [real0, imaginary0, real1, imaginary1, ...] pairs, including zero imaginary components."),
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
        using var ownership = receipts.AcquireSession(instanceId);
        SimulationJobState result;
        try
        {
            result = await ReadNative(client, instanceId, session.Epoch, target, jobId.ToString("D"), cancellationToken);
        }
        catch (AutomationException error) when (error.Code == "simulation_result_not_found")
        {
            throw new AutomationException("simulation_unavailable", error.Message);
        }
        var operation = receipts.ReadOperation(instanceId, Guid.Parse(result.OperationId));
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(operation), job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_vector", ReadOnly = true),
     Description("Read one exact named vector from a completed native or archived simulation job. Supply the original process epoch, schematic document and job ID. Complex vectors preserve their native complex=true type and return ordered real/imaginary pairs plus separate realValues and imaginaryValues; real vectors remain flat samples with an empty imaginaryValues array. sampleStart and sampleCount address logical samples, so a complex pair can never be split. An archived result is read without contacting KiCad and remains available after native or MCP restart."),
     KiCadCapability("simulation", "durable-results", "original instance, process epoch, document, job ID, vector name"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "SimulationDeckAdmissionTests.ExtractsNamedVectorSlicesWithoutSplittingComplexPairs")]
    public Task<CallToolResult> Vector(string instanceId, string processEpoch, SimulationDocumentInput document,
        Guid jobId, string vectorName, int sampleStart = 0, int sampleCount = -1,
        CancellationToken cancellationToken = default) => Execute(async () =>
    {
        ValidateId(jobId, "job");
        if (string.IsNullOrWhiteSpace(processEpoch))
            throw new AutomationException("invalid_simulation_request", "A nonempty simulation process epoch is required.");
        var target = SimulationDocumentInput.ToNative(document);
        using var ownership = receipts.AcquireSession(instanceId);
        if (receipts.ReadTerminal(instanceId, processEpoch, jobId) is { } archive)
        {
            if (!archive.Operation.Document.Equals(target))
                throw new AutomationException("simulation_result_not_found",
                    "No native or archived simulation result matches the original schematic document.");
            return VectorData(archive, target, vectorName, sampleStart, sampleCount);
        }

        NativeClient client;
        try { client = instances.Client(instanceId); }
        catch (AutomationException)
        {
            throw new AutomationException("simulation_result_not_found",
                "No native or archived simulation result matches the original instance, process epoch, document and job.");
        }
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId)
            throw new AutomationException("simulation_instance_changed", "The native instance identity changed; inspect it again.");
        if (session.Epoch != processEpoch)
        {
            if (receipts.HasEarlierEpoch(instanceId, session.Epoch, jobId))
                throw ResultLostAfterRestart(jobId.ToString("D"));
            throw new AutomationException("simulation_result_not_found",
                "No native or archived simulation result matches the requested process epoch.");
        }
        var result = await ReadNative(client, instanceId, session.Epoch, target, jobId.ToString("D"), cancellationToken);
        var operation = OperationFor(instanceId, target, session.Epoch, result.OperationId);
        var slice = SimulationJobContract.ExtractVector(result, vectorName, sampleStart, sampleCount);
        return Data(new
        {
            instanceId, instanceEpoch = session.Epoch, document = target, source = "native",
            receiptSha256 = (string?)null, operationId = result.OperationId, jobId = result.JobId,
            deck = DescribeDeck(operation), vector = DescribeVector(slice)
        });
    });

    [McpServerTool(Name = "kicad_simulation_operation", ReadOnly = true),
     Description("Look up one process-owned native simulation by its original operation ID after a lost start reply. The lookup is bound to the current instance epoch and complete schematic document, returns found=false when the native process has no authoritative record, and never starts or replays a simulation."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, operation ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public Task<CallToolResult> Operation(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        Guid operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateId(operationId, "operation");
        var target = SimulationDocumentInput.ToNative(document);
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        using var ownership = receipts.AcquireSession(instanceId);
        var result = await LookupNative(client, target, session.Epoch, operationId.ToString("D"), cancellationToken);
        if (result is null)
            return Data(new { instanceId, instanceEpoch = session.Epoch, document = target,
                operationId = operationId.ToString("D"), found = false, lookupSupported = true });
        if (Terminal(result))
            await CaptureTerminal(client, instanceId, target, session.Epoch, result, cancellationToken);
        var operation = OperationFor(instanceId, target, session.Epoch, result.OperationId);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target,
            operationId = result.OperationId, found = true, lookupSupported = true,
            deck = DescribeDeck(operation), job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_result", ReadOnly = true),
     Description("Read an immutable archived terminal simulation result without contacting KiCad. Supply its original instanceId, processEpoch, complete schematic document and jobId. The archive preserves the original native outcome, vectors and captured deck/dependency evidence, including incomplete coverage and warnings; it does not adopt a replacement editor or resume a simulation. Complex vectors retain ordered real/imaginary pairs and their native type; historical real-only receipts are never reclassified or reconstructed."),
     KiCadCapability("simulation", "durable-results", "original instance, process epoch, document, job ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public Task<CallToolResult> Result(string instanceId, string processEpoch, SimulationDocumentInput document,
        Guid jobId, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateId(jobId, "job");
        var target = SimulationDocumentInput.ToNative(document);
        var archive = receipts.ReadTerminal(instanceId, processEpoch, jobId);
        if (archive is null || !archive.Operation.Document.Equals(target))
            throw new AutomationException("simulation_result_not_found", "No terminal simulation archive matches that original instance, process epoch, document and job.");
        return Task.FromResult(ArchivedData(archive, target));
    });

    [McpServerTool(Name = "kicad_simulation_dependencies", ReadOnly = true),
     Description("Read the dependency evidence for one exact native KiCad simulation job. Pass the complete native document descriptor as protobuf JSON in documentJson, including its exact sheet-instance path. The response is bound to the process epoch, document, job, and original operation. A completed archive is read after native or MCP restart without contacting a replacement editor. A false dependencyCaptureComplete value is returned when KiCad or the retained deck has not enumerated external dependency paths; warnings remain explicit and are never converted into an empty successful dependency set."),
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
        using var ownership = receipts.AcquireSession(instanceId);
        if (receipts.ReadTerminal(instanceId, expectedInstanceEpoch, jobId) is { } archive)
        {
            if (!archive.Operation.Document.Equals(document))
                throw new AutomationException("simulation_result_not_found",
                    "No native or archived simulation result matches the original schematic document.");
            return ArchivedDependenciesData(archive, document);
        }
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
        SimulationJobContract.ValidateDependencies(result, document, session.Epoch, jobId.ToString("D"));
        var operation = OperationFor(instanceId, document, session.Epoch, result.OperationId);
        SimulationJobContract.ValidateDependencies(result, document, session.Epoch, jobId.ToString("D"),
            operation?.OperationId, operation?.RequestedDeckSha256);
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
     Description("Wait for one exact native simulation job to change state. A matching immutable terminal archive is returned before contacting a current or replacement editor, including when afterSequence already reaches the final sequence. This targets only the simulator job, not the design; cancellation or timeout never becomes a successful result."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, job ID, sequence"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public async Task<CallToolResult> Wait(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        Guid jobId, ulong afterSequence, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateId(jobId, "job");
            var target = SimulationDocumentInput.ToNative(document);
            // A terminal result remains final even when the original editor is
            // gone or the caller has already observed its last sequence.
            using (var ownership = receipts.AcquireSession(instanceId))
            {
                if (receipts.ReadTerminal(instanceId, expectedInstanceEpoch, jobId) is { } archive)
                {
                    if (!archive.Operation.Document.Equals(target))
                        throw new AutomationException("simulation_wait_incomplete",
                            "The terminal simulation archive belongs to another schematic document; no wait result is available for this target.");
                    return ArchivedData(archive, target);
                }
            }
            var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
            if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
                throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                {
                    // Release ownership between reads so cancellation can reach
                    // the native simulator while this wait is pending.
                    using var ownership = receipts.AcquireSession(instanceId);
                    var result = await ReadNative(client, instanceId, session.Epoch, target, jobId.ToString("D"), deadline.Token);
                    var operation = OperationFor(instanceId, target, session.Epoch, result.OperationId);
                    if (result.Sequence > afterSequence || Terminal(result))
                        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target,
                            deck = DescribeDeck(operation), job = Describe(result) });
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or OperationCanceledException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "simulation_wait_incomplete", errorMessage = error.Message }, true); }
    }

    [McpServerTool(Name = "kicad_simulation_cancel"),
     Description("Cancel one exact native KiCad simulation job. A matching immutable terminal archive is returned before contacting a current or replacement editor, so a late completion or retry cannot cancel another job. Otherwise the native simulator is stopped before the terminal Cancelled state is returned; result vectors from a cancelled run are never reported as successful output."),
     KiCadCapability("simulation", "native-ngspice", "instance epoch, document, job ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.NativeSimulationUsesKiCadNgspiceAndReturnsVectors")]
    public Task<CallToolResult> Cancel(string instanceId, string expectedInstanceEpoch, SimulationDocumentInput document,
        Guid jobId, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateId(jobId, "job");
        var target = SimulationDocumentInput.ToNative(document);
        // A terminal archive is authoritative for this original job. Check it
        // before resolving the live instance so a late cancellation retry after
        // a native/MCP restart cannot contact (or cancel) a replacement editor.
        using var ownership = receipts.AcquireSession(instanceId);
        if (receipts.ReadTerminal(instanceId, expectedInstanceEpoch, jobId) is { } archive)
        {
            if (!archive.Operation.Document.Equals(target))
                throw new AutomationException("simulation_result_not_found",
                    "No terminal simulation archive matches that original schematic document.");
            return ArchivedData(archive, target);
        }
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var request = new CancelSimulationJob
        { Document = target, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch };
        SimulationJobState result;
        try { result = await client.InvokeAsync<CancelSimulationJob, SimulationJobState>(request, cancellationToken); }
        catch (NativeApiException error) when (IsMissingJob(error) && receipts.HasEarlierEpoch(instanceId, session.Epoch, jobId))
        {
            throw ResultLostAfterRestart(request.JobId);
        }
        SimulationJobContract.ValidateRead(result, target, session.Epoch, jobId.ToString("D"));
        var operation = OperationFor(instanceId, target, session.Epoch, result.OperationId);
        if (Terminal(result))
            result = await ReadNative(client, instanceId, session.Epoch, target, result.JobId, cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = target, deck = DescribeDeck(operation), job = Describe(result) });
    });

    private async Task PreserveCurrent(NativeClient client, string instanceId, string epoch, string nextOperationId,
        CancellationToken cancellationToken)
    {
        var current = receipts.ReadCurrent(instanceId, epoch);
        if (current is null || current.OperationId == nextOperationId || current.Stage == SimulationOperationStage.Prepared
            || receipts.ReadTerminal(current) is not null) return;
        if (current.JobId is null) throw SimulationResultReceipts.Unresolved(current.OperationId);
        var result = await ReadNative(client, instanceId, epoch, current.Document, current.JobId, cancellationToken);
        if (!Terminal(result)) throw new AutomationException("simulation_busy", "The preceding simulation is still running; wait or cancel it before starting another operation.");
    }

    private async Task<SimulationJobState?> LookupNative(NativeClient client, DocumentSpecifier document,
        string epoch, string operationId, CancellationToken cancellationToken)
    {
        NativeCapabilitySimulationReadOperationResponse response;
        try
        {
            response = await client.InvokeAsync<NativeCapabilitySimulationReadOperation,
                NativeCapabilitySimulationReadOperationResponse>(new()
                { Document = document, OperationId = operationId, ProcessEpoch = epoch }, cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == (int)ApiStatusCode.AsUnhandled
            || error.Status == (int)ApiStatusCode.AsUnimplemented)
        {
            throw new AutomationException("simulation_operation_lookup_unavailable",
                "The connected KiCad peer does not provide native simulation operation lookup.");
        }

        if (!response.Found) return null;
        if (response.Job is null)
            throw new AutomationException("invalid_simulation_result",
                "The native simulation operation lookup reported a job without its state.");
        SimulationJobContract.ValidateRead(response.Job, document, epoch, response.Job.JobId, operationId);
        return response.Job;
    }

    private async Task<SimulationJobState> ReadNative(NativeClient client, string instanceId, string epoch,
        DocumentSpecifier document, string jobId, CancellationToken cancellationToken)
    {
        SimulationJobState result;
        try
        {
            result = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = document, JobId = jobId, ProcessEpoch = epoch }, cancellationToken);
        }
        catch (NativeApiException error) when (IsMissingJob(error) && receipts.HasEarlierEpoch(instanceId, epoch, Guid.Parse(jobId)))
        { throw ResultLostAfterRestart(jobId); }
        SimulationJobContract.ValidateRead(result, document, epoch, jobId);
        var operation = OperationFor(instanceId, document, epoch, result.OperationId);
        SimulationJobContract.ValidateRead(result, document, epoch, jobId, operation?.OperationId);
        if (Terminal(result)) await CaptureTerminal(client, instanceId, document, epoch, result, cancellationToken);
        return result;
    }

    private async Task CaptureTerminal(NativeClient client, string instanceId, DocumentSpecifier document,
        string epoch, SimulationJobState result, CancellationToken cancellationToken)
    {
        if (receipts.ReadTerminal(instanceId, epoch, Guid.Parse(result.JobId)) is { } existing)
        {
            SimulationJobContract.ValidateArchive(existing, document, epoch, result.JobId);
            if (existing.Job.OperationId != result.OperationId || existing.Job.Status != result.Status)
                throw new AutomationException("invalid_simulation_result",
                    "The native terminal operation changed after its immutable archive was recorded.");
            return;
        }

        var operation = OperationFor(instanceId, document, epoch, result.OperationId);
        if (operation is null)
        {
            // Retain native evidence without inventing caller request bytes when
            // another state directory originally started the job.
            operation = SimulationOperationReceipt.Observe(instanceId, result);
            receipts.WriteOperation(operation);
        }
        else if (operation.Stage == SimulationOperationStage.Dispatched)
        {
            operation = operation with { Stage = SimulationOperationStage.Accepted, JobId = result.JobId };
            receipts.WriteOperation(operation);
        }
        if (operation.JobId != result.JobId)
            throw new AutomationException("invalid_simulation_result", "The native result changed its retained operation's job identity.");
        var dependencies = await client.InvokeAsync<NativeCapabilitySimulationReadDependencies, NativeCapabilitySimulationDependencies>(new()
        { Document = document, JobId = result.JobId, ProcessEpoch = epoch }, cancellationToken);
        SimulationJobContract.ValidateDependencies(dependencies, document, epoch, result.JobId,
            operation.OperationId, operation.RequestedDeckSha256);
        receipts.WriteTerminal(SimulationTerminalReceipt.Capture(operation, result, dependencies));
    }

    private SimulationOperationReceipt? OperationFor(string instanceId, DocumentSpecifier document, string epoch, string operationId)
    {
        var operation = receipts.ReadOperation(instanceId, Guid.Parse(operationId));
        if (operation is not null && (operation.ProcessEpoch != epoch || !operation.Document.Equals(document)))
            throw new AutomationException("invalid_simulation_result", "The native operation does not match its retained original process and schematic identity.");
        return operation;
    }

    private static bool Terminal(SimulationJobState result) => result.Status is SimulationJobStatus.SimjsCompleted
        or SimulationJobStatus.SimjsCancelled or SimulationJobStatus.SimjsFailed;

    private static CallToolResult ArchivedData(SimulationTerminalReceipt receipt, DocumentSpecifier document)
    {
        var operation = receipt.Operation; var job = receipt.Job; var dependencies = receipt.Dependencies;
        SimulationJobContract.ValidateArchive(receipt, document, operation.ProcessEpoch, job.JobId);
        return Data(new
        {
            instanceId = operation.InstanceId, instanceEpoch = operation.ProcessEpoch, document,
            source = "archive", receiptSha256 = receipt.Sha256,
            deck = DescribeDeck(operation), job = Describe(job),
            dependencies = new
            {
                document = dependencies.Document, processEpoch = dependencies.ProcessEpoch,
                jobId = dependencies.JobId, operationId = dependencies.OperationId,
                generatedDeckSha256 = dependencies.GeneratedDeckSha256, dependencyCaptureComplete = dependencies.DependencyCaptureComplete,
                dependencyPaths = dependencies.DependencyPaths.ToArray(), warnings = dependencies.Warnings.ToArray()
            }
        });
    }

    private static CallToolResult VectorData(SimulationTerminalReceipt receipt, DocumentSpecifier document,
        string vectorName, int sampleStart, int sampleCount)
    {
        var operation = receipt.Operation;
        SimulationJobContract.ValidateArchive(receipt, document, operation.ProcessEpoch, receipt.Job.JobId);
        var slice = SimulationJobContract.ExtractVector(receipt.Job, vectorName, sampleStart, sampleCount);
        return Data(new
        {
            instanceId = operation.InstanceId, instanceEpoch = operation.ProcessEpoch, document,
            source = "archive", receiptSha256 = receipt.Sha256, operationId = operation.OperationId,
            jobId = receipt.Job.JobId, deck = DescribeDeck(operation), vector = DescribeVector(slice)
        });
    }

    private static CallToolResult ArchivedDependenciesData(SimulationTerminalReceipt receipt,
        DocumentSpecifier document)
    {
        var operation = receipt.Operation;
        var dependencies = receipt.Dependencies;
        SimulationJobContract.ValidateArchive(receipt, document, operation.ProcessEpoch, receipt.Job.JobId);
        return Data(new
        {
            instanceId = operation.InstanceId,
            instanceEpoch = operation.ProcessEpoch,
            document,
            source = "archive",
            receiptSha256 = receipt.Sha256,
            jobId = dependencies.JobId,
            operationId = dependencies.OperationId,
            generatedDeckSha256 = dependencies.GeneratedDeckSha256,
            dependencyCaptureComplete = dependencies.DependencyCaptureComplete,
            dependencyPaths = dependencies.DependencyPaths.ToArray(),
            warnings = dependencies.Warnings.ToArray()
        });
    }

    private static object Describe(SimulationJobState value) => new
    {
        jobId = value.JobId, operationId = value.OperationId, status = value.Status.ToString(), sequence = value.Sequence,
        progress = value.Progress, cancellationRequested = value.CancellationRequested, workerFinished = value.WorkerFinished,
        errorCode = value.ErrorCode, errorMessage = value.ErrorMessage, messages = value.Messages,
        vectors = value.Status == SimulationJobStatus.SimjsCompleted ? value.Vectors.Select(v => new { name = v.Name, complex = v.Complex, values = v.Values }).ToArray() : []
    };

    private static object? DescribeDeck(SimulationOperationReceipt? operation)
        => operation is { RequestFingerprintKnown: true }
            ? new { source = operation.DeckSource, sha256 = operation.RequestedDeckSha256, externalFiles = Array.Empty<string>(),
                selfContained = operation.DeckSource == "inline-caller-deck", dependenciesKnown = operation.DeckSource == "inline-caller-deck",
                operationId = operation.OperationId }
            : null;

    private static object DescribeVector(SimulationJobContract.VectorSlice slice) => new
    {
        name = slice.Name, complex = slice.Complex,
        encoding = slice.Complex ? "real-imaginary-pairs" : "real-samples",
        sampleStart = slice.SampleStart, sampleCount = slice.SampleCount,
        totalSampleCount = slice.TotalSampleCount,
        values = slice.Values, realValues = slice.RealValues, imaginaryValues = slice.ImaginaryValues
    };


    private static bool IsMissingJob(NativeApiException error)
        => error.Message.Contains("no handler available for request of type kiapi.automation.v1.ReadSimulationJob",
               StringComparison.OrdinalIgnoreCase)
            || (error.Status == (int)ApiStatusCode.AsBadRequest
                && error.Message.Contains("No simulation job matches", StringComparison.OrdinalIgnoreCase));

    private static AutomationException ResultLostAfterRestart(string jobId)
        => new("simulation_result_lost",
            $"The native process restarted before simulation job {jobId} could be read; its result was process-owned and is no longer retained.");

    private static void ValidateId(Guid id, string kind)
    {
        if (id == Guid.Empty)
            throw new AutomationException("invalid_simulation_request", "A nonempty simulation " + kind + " ID is required.");
    }


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
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native ? NativeErrorCode(native) : "simulation_unavailable";
            return Data(new { errorCode = code, errorMessage = error.Message }, true);
        }
    }

    private static string NativeErrorCode(NativeApiException error)
    {
        if (error.Status == (int)ApiStatusCode.AsBusy)
            return "simulation_busy";
        if (error.Status == (int)ApiStatusCode.AsBadRequest
            && error.Message.Contains("operation identity was reused", StringComparison.OrdinalIgnoreCase))
            return "simulation_operation_reused";
        return "simulation_unavailable";
    }
}
