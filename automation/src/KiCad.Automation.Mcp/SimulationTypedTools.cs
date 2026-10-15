using System.ComponentModel;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed partial class SimulationTools
{
    [McpServerTool(Name = "kicad_simulation_start_typed"),
     Description("Start a KiCad simulation with a structured schematic document, original process epoch and operation ID. An empty netlist uses KiCad's generated deck. Supplied decks retain the existing admission checks; identical completed operations replay their immutable receipt and an unresolved dispatch is never automatically rerun."),
     KiCadCapability("simulation", "native-ngspice", "structured document, process epoch, operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedLifecyclePublishesSchemasAndRefusesInvalidBindings")]
    public Task<CallToolResult> StartTyped(string instanceId, SimulationStartInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Start(instanceId, binding.ProcessEpoch, binding.Document, input.Netlist ?? "",
            binding.OperationId, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_job_typed", ReadOnly = true),
     Description("Read the exact native simulation job identified by a structured schematic document, original process epoch and job ID. Only a completed job returns result vectors."),
     KiCadCapability("simulation", "native-ngspice", "structured document, process epoch, job ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedLifecyclePublishesSchemasAndRefusesInvalidBindings")]
    public Task<CallToolResult> JobTyped(string instanceId, SimulationJobInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Job(instanceId, binding.ProcessEpoch, binding.Document, binding.JobId, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_operation_typed", ReadOnly = true),
     Description("Recover the identity of a native simulation after a lost start reply using its structured schematic document, original process epoch and operation ID. This lookup does not dispatch a simulation."),
     KiCadCapability("simulation", "native-ngspice", "structured document, process epoch, operation ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedLifecyclePublishesSchemasAndRefusesInvalidBindings")]
    public Task<CallToolResult> OperationTyped(string instanceId, SimulationOperationInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Operation(instanceId, binding.ProcessEpoch, binding.Document, binding.OperationId, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_wait_typed", ReadOnly = true),
     Description("Wait for the exact native simulation job to advance beyond afterSequence, using a structured schematic document, process epoch and job ID. A matching terminal archive returns immediately after native or MCP restart, even at the final sequence. Cancellation or the existing five-minute deadline remains an incomplete wait, never a completed simulation."),
     KiCadCapability("simulation", "native-ngspice", "structured document, process epoch, job ID, sequence"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedLifecyclePublishesSchemasAndRefusesInvalidBindings")]
    public Task<CallToolResult> WaitTyped(string instanceId, SimulationWaitInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Wait(instanceId, binding.ProcessEpoch, binding.Document, binding.JobId,
            input.AfterSequence, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_cancel_typed"),
     Description("Stop the exact native simulation identified by a structured schematic document, process epoch and job ID. The existing native cancellation and terminal-result capture checks apply."),
     KiCadCapability("simulation", "native-ngspice", "structured document, process epoch, job ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedLifecyclePublishesSchemasAndRefusesInvalidBindings")]
    public Task<CallToolResult> CancelTyped(string instanceId, SimulationJobInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Cancel(instanceId, binding.ProcessEpoch, binding.Document, binding.JobId, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_result_typed", ReadOnly = true),
     Description("Read an immutable terminal simulation archive with its structured original schematic document, process epoch and job ID. Native and MCP restarts cannot rebind the result to a replacement editor; complex vector pairs and dependency warnings are preserved."),
     KiCadCapability("simulation", "durable-results", "structured original document, process epoch, job ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedArchiveReplayPreservesOriginalIdentityAndComplexSamples")]
    public Task<CallToolResult> ResultTyped(string instanceId, SimulationJobInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Result(instanceId, binding.ProcessEpoch, binding.Document, binding.JobId, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_vector_typed", ReadOnly = true),
     Description("Read a named vector from the exact native or archived simulation using a structured original schematic document, process epoch and job ID. Sample ranges address logical samples and preserve each real/imaginary pair."),
     KiCadCapability("simulation", "durable-results", "structured original document, process epoch, job ID, vector name"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedArchiveReplayPreservesOriginalIdentityAndComplexSamples")]
    public Task<CallToolResult> VectorTyped(string instanceId, SimulationVectorInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        if (string.IsNullOrWhiteSpace(input.VectorName) || input.SampleStart < 0 || input.SampleCount < -1)
            throw new AutomationException("invalid_simulation_request", "A vector name and a valid logical sample range are required.");
        return Vector(instanceId, binding.ProcessEpoch, binding.Document, binding.JobId,
            input.VectorName, input.SampleStart, input.SampleCount, cancellationToken);
    });

    [McpServerTool(Name = "kicad_simulation_dependencies_typed", ReadOnly = true),
     Description("Read native simulation dependency evidence with a structured schematic document, process epoch and job ID. The original operation and generated deck hash remain bound to that job; incomplete dependency capture stays explicit."),
     KiCadCapability("simulation", "dependency-evidence", "structured document, process epoch, job ID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SimulationTypedMcpProcessTests.TypedLifecyclePublishesSchemasAndRefusesInvalidBindings")]
    public Task<CallToolResult> DependenciesTyped(string instanceId, SimulationJobInput input,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var binding = Required(input).Bind();
        return Dependencies(instanceId, binding.ProcessEpoch,
            SchematicJson.Formatter.Format(SimulationDocumentInput.ToNative(binding.Document)),
            binding.JobId, cancellationToken);
    });

    private static T Required<T>(T? input) where T : class => input
        ?? throw new AutomationException("invalid_simulation_request", "A structured simulation input is required.");
}

public abstract class SimulationProcessInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private string? processEpoch;

    [Description("Complete schematic document identity, including the project and every sheet-instance UUID.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }

    [Description("Original native process epoch as a nonempty canonical lowercase UUID. Archive reads use the original epoch.")]
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }

    internal (SimulationDocumentInput Document, string ProcessEpoch) BindProcess() =>
        (SimulationTypedInputValidation.Document(Document),
         SimulationTypedInputValidation.Identifier(ProcessEpoch, "process epoch").ToString("D"));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class SimulationOperationInput : SimulationProcessInput
{
    private string? operationId;
    [Description("Original simulation operation identity as a nonempty canonical lowercase UUID.")]
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }

    internal (SimulationDocumentInput Document, string ProcessEpoch, Guid OperationId) Bind()
    {
        var process = BindProcess();
        return (process.Document, process.ProcessEpoch, SimulationTypedInputValidation.Identifier(OperationId, "operation"));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationStartInput : SimulationOperationInput
{
    private string? netlist;
    [Description("Empty or omitted uses KiCad's schematic-generated deck. Inline plain circuits retain the existing deck admission checks.")]
    public string? Netlist { get => netlist; set => netlist = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class SimulationJobInput : SimulationProcessInput
{
    private string? jobId;
    [Description("Original native simulation job identity as a nonempty canonical lowercase UUID.")]
    public string? JobId { get => jobId; set => jobId = BindOnce(value); }

    internal (SimulationDocumentInput Document, string ProcessEpoch, Guid JobId) Bind()
    {
        var process = BindProcess();
        return (process.Document, process.ProcessEpoch, SimulationTypedInputValidation.Identifier(JobId, "job"));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationWaitInput : SimulationJobInput
{
    private ulong afterSequence;
    [Description("Last observed native job sequence. Return when that exact job advances or becomes terminal.")]
    public ulong AfterSequence { get => afterSequence; set => afterSequence = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationVectorInput : SimulationJobInput
{
    private string? vectorName;
    private int sampleStart;
    private int sampleCount = -1;

    public string? VectorName { get => vectorName; set => vectorName = BindOnce(value); }
    public int SampleStart { get => sampleStart; set => sampleStart = BindOnce(value); }
    [Description("Logical sample count. -1 reads the rest of the vector; zero reads an empty slice.")]
    public int SampleCount { get => sampleCount; set => sampleCount = BindOnce(value); }
}

internal static class SimulationTypedInputValidation
{
    internal static SimulationDocumentInput Document(NativeDocumentInput? input)
    {
        DocumentSpecifier target = NativeDocumentInput.ToNative(input);
        if (target.Type != DocumentType.DoctypeSchematic)
            throw new AutomationException("unsupported_simulation_document", "Simulation requires a schematic document.");
        // Adapt only the validated document. This preserves the existing native
        // execution and receipt paths, including the complete ordered sheet path.
        return new()
        {
            Type = SimulationDocumentType.Schematic,
            Project = new() { Name = target.Project.Name, Path = target.Project.Path },
            SheetPath = new()
            {
                Path = target.SheetPath.Path.Select(id => (SimulationUuidInput?)new SimulationUuidInput { Value = id.Value }).ToList(),
                PathHumanReadable = target.SheetPath.PathHumanReadable
            }
        };
    }

    internal static Guid Identifier(string? value, string kind)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || value != id.ToString("D"))
            throw new AutomationException("invalid_simulation_request", "A nonempty canonical lowercase simulation " + kind + " UUID is required.");
        return id;
    }
}
