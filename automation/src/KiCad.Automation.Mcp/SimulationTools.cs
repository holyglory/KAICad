using System.ComponentModel;
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
    [McpServerTool(Name = "kicad_simulation_start"),
     Description("Start one explicit ngspice simulation through KiCad's existing native simulator for a verified schematic document. Pass an empty netlist to simulate the deck KiCad generates from the schematic. A supplied netlist must be a plain circuit: interpreter blocks (.control/.exec, *ng_script) and file includes (.include, .lib file) are rejected. The native editor owns the simulator and its result vectors. Retry the same operation ID after a lost reply; a different operation cannot run concurrently in this document.")]
    public Task<CallToolResult> Start(string instanceId, string expectedInstanceEpoch, DocumentSpecifier document,
        string netlist, Guid operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        SimulationDeckAdmission.Validate(netlist);
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(new()
        {
            Document = document, OperationId = operationId.ToString("D"), ProcessEpoch = session.Epoch, Netlist = netlist
        }, cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = document, job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_job", ReadOnly = true),
     Description("Read one exact native KiCad simulation job by process epoch and job ID. Only a completed job exposes copied result vectors; running, cancelled and failed states remain explicit.")]
    public Task<CallToolResult> Job(string instanceId, string expectedInstanceEpoch, DocumentSpecifier document,
        Guid jobId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
        { Document = document, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch }, cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = document, job = Describe(result) });
    });

    [McpServerTool(Name = "kicad_simulation_wait", ReadOnly = true),
     Description("Wait for one exact native simulation job to change state. This targets only the simulator job, not the design; cancellation or timeout never becomes a successful result.")]
    public async Task<CallToolResult> Wait(string instanceId, string expectedInstanceEpoch, DocumentSpecifier document,
        Guid jobId, ulong afterSequence, CancellationToken cancellationToken)
    {
        try
        {
            var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
            if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
                throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var result = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
                { Document = document, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch }, deadline.Token);
                if (result.Sequence > afterSequence || result.Status is SimulationJobStatus.SimjsCompleted or SimulationJobStatus.SimjsCancelled or SimulationJobStatus.SimjsFailed)
                    return Data(new { instanceId, instanceEpoch = session.Epoch, document = document, job = Describe(result) });
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or OperationCanceledException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "simulation_wait_incomplete", errorMessage = error.Message }, true); }
    }

    [McpServerTool(Name = "kicad_simulation_cancel"),
     Description("Cancel one exact native KiCad simulation job. The native simulator is stopped before the terminal Cancelled state is returned; result vectors from a cancelled run are never reported as successful output.")]
    public Task<CallToolResult> Cancel(string instanceId, string expectedInstanceEpoch, DocumentSpecifier document,
        Guid jobId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var client = instances.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != instanceId || session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("simulation_instance_changed", "The native instance identity or epoch changed; inspect it again.");
        var result = await client.InvokeAsync<CancelSimulationJob, SimulationJobState>(new()
        { Document = document, JobId = jobId.ToString("D"), ProcessEpoch = session.Epoch }, cancellationToken);
        return Data(new { instanceId, instanceEpoch = session.Epoch, document = document, job = Describe(result) });
    });

    private static object Describe(SimulationJobState value) => new
    {
        jobId = value.JobId, operationId = value.OperationId, status = value.Status.ToString(), sequence = value.Sequence,
        progress = value.Progress, cancellationRequested = value.CancellationRequested, workerFinished = value.WorkerFinished,
        errorCode = value.ErrorCode, errorMessage = value.ErrorMessage, messages = value.Messages,
        vectors = value.Status == SimulationJobStatus.SimjsCompleted ? value.Vectors.Select(v => new { name = v.Name, complex = v.Complex, values = v.Values }).ToArray() : []
    };

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
