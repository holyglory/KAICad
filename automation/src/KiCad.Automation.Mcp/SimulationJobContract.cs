using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Validates the identity and observable terminal state of a native simulator
/// job before the MCP exposes it.  KiCad owns the simulator and its vectors;
/// this boundary prevents a stale job or a partial vector snapshot from being
/// reported as a result for another schematic.
/// </summary>
internal static class SimulationJobContract
{
    public static void ValidateStart(StartSimulationJob request, SimulationJobState result,
        string processEpoch)
    {
        ValidateStartIdentity(request, result, processEpoch);
        ValidateState(result);
    }

    public static void ValidateStartIdentity(StartSimulationJob request, SimulationJobState result,
        string processEpoch)
    {
        if (request is null || result is null)
            throw Invalid("A simulation request and native result are required.");

        ValidateIdentity(result, request.Document, request.OperationId, processEpoch, result.JobId);
    }

    public static void ValidateRead(SimulationJobState result, DocumentSpecifier document,
        string processEpoch, string jobId, string? expectedOperationId = null)
    {
        if (result is null) throw Invalid("A native simulation result is required.");
        ValidateIdentity(result, document, expectedOperationId, processEpoch, jobId);
        if (!string.IsNullOrWhiteSpace(expectedOperationId) && result.OperationId != expectedOperationId)
            throw Invalid("The native simulation operation identity changed while reading the job.");
        ValidateState(result);
    }

    public static void ValidateDependencies(NativeCapabilitySimulationDependencies result,
        DocumentSpecifier document, string processEpoch, string jobId,
        string? expectedOperationId = null, string? expectedDeckSha256 = null)
    {
        if (result is null) throw Invalid("Native simulation dependency evidence is required.");

        DocumentStateTools.ValidateTarget(document);
        if ((int)document.Type != 1)
            throw new AutomationException("unsupported_simulation_document", "Simulation requires a schematic document.");
        if (!document.Equals(result.Document) || result.JobId != jobId || result.ProcessEpoch != processEpoch)
            throw Invalid("The native simulation dependency evidence does not match the requested schematic, process epoch or job.");
        if (!Guid.TryParseExact(result.JobId, "D", out var job) || job == Guid.Empty
            || !Guid.TryParseExact(result.OperationId, "D", out var operation) || operation == Guid.Empty)
            throw Invalid("The native simulation dependency evidence lacks canonical job and operation identities.");
        if (expectedOperationId is not null && result.OperationId != expectedOperationId)
            throw Invalid("The native simulation dependency evidence changed operation identity.");

        if (!Sha256(result.GeneratedDeckSha256))
            throw Invalid("The native simulation dependency evidence lacks a lowercase SHA-256 deck fingerprint.");
        if (expectedDeckSha256 is not null && result.GeneratedDeckSha256 != expectedDeckSha256)
            throw Invalid("The native simulation dependency evidence does not match the retained deck fingerprint.");

        if (result.DependencyPaths.Any(string.IsNullOrWhiteSpace)
            || result.DependencyPaths.Distinct(StringComparer.Ordinal).Count() != result.DependencyPaths.Count)
            throw Invalid("The native simulation dependency evidence contains duplicate or unnamed paths.");
        if (result.Warnings.Any(string.IsNullOrWhiteSpace)
            || result.Warnings.Distinct(StringComparer.Ordinal).Count() != result.Warnings.Count)
            throw Invalid("The native simulation dependency evidence contains duplicate or unnamed warnings.");
        if (result.DependencyCaptureComplete && result.Warnings.Count != 0)
            throw Invalid("Complete native simulation dependency evidence cannot carry warnings.");
        if (!result.DependencyCaptureComplete && result.Warnings.Count == 0)
            throw Invalid("Incomplete native simulation dependency evidence must retain an explicit warning.");
    }

    private static void ValidateIdentity(SimulationJobState result, DocumentSpecifier document,
        string? operationId, string processEpoch, string jobId)
    {
        DocumentStateTools.ValidateTarget(document);
        if ((int)document.Type != 1)
            throw new AutomationException("unsupported_simulation_document", "Simulation requires a schematic document.");
        if (!document.Equals(result.Document) || result.ProcessEpoch != processEpoch
            || result.JobId != jobId || string.IsNullOrWhiteSpace(result.OperationId)
            || (operationId is not null && result.OperationId != operationId))
            throw Invalid("The native simulation result does not match the requested schematic, process epoch, job or operation.");
        if (!Guid.TryParseExact(result.JobId, "D", out var job) || job == Guid.Empty
            || !Guid.TryParseExact(result.OperationId, "D", out var operation) || operation == Guid.Empty)
            throw Invalid("The native simulation result lacks canonical job and operation identities.");
    }

    private static void ValidateState(SimulationJobState result)
    {
        if (!Enum.IsDefined(result.Status) || result.Status == SimulationJobStatus.SimjsUnknown)
            throw Invalid("The native simulation result has an unknown status.");
        if (!double.IsFinite(result.Progress) || result.Progress < 0 || result.Progress > 100)
            throw Invalid("The native simulation result has a non-finite or out-of-range progress value.");

        bool terminal = result.Status is SimulationJobStatus.SimjsCompleted
            or SimulationJobStatus.SimjsCancelled or SimulationJobStatus.SimjsFailed;
        if (terminal != result.WorkerFinished)
            throw Invalid("The native simulation terminal state does not match worker completion.");

        if (result.Status != SimulationJobStatus.SimjsCompleted && result.Vectors.Count != 0)
            throw Invalid("Only a completed native simulation may expose result vectors.");

        if (result.Vectors.Any(vector => string.IsNullOrWhiteSpace(vector.Name))
            || result.Vectors.Select(vector => vector.Name).Distinct(StringComparer.Ordinal).Count() != result.Vectors.Count)
            throw Invalid("The native simulation returned duplicate or unnamed vectors.");

        if (result.Status == SimulationJobStatus.SimjsCompleted
            && result.Vectors.Any(vector => !vector.Values.All(double.IsFinite)
                || (vector.Complex && vector.Values.Count % 2 != 0)))
            throw Invalid("The completed native simulation returned non-finite or malformed vector values.");
    }

    private static bool Sha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static AutomationException Invalid(string message) => new("invalid_simulation_result", message);
}
