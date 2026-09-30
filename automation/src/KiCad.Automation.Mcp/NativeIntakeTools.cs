using System.ComponentModel;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class NativeIntakeTools(NativeIntakeRegistry registry)
{
    [McpServerTool(Name = "kicad_design_native_intake_start"),
     Description("Start continuous native-event observation for an explicit attached instance and absolute recovery path. The existing recovery record supplies the exact document target. Verifies the process and event stream, subscribes before taking a snapshot, and saves committed native changes into recovery state between calls. Preserves desired XML bytes, baseline, requirements and pending operations. Does not write design XML or mutate KiCad; this is not complete bidirectional synchronization. Returns a process-local intake ID. Duplicate recovery paths are rejected; use list after a lost reply.")]
    public Task<CallToolResult> Start(string instanceId, string recoveryPath, CancellationToken cancellationToken) => Execute(async () =>
    {
        string intakeId = await registry.StartAsync(instanceId, recoveryPath, cancellationToken);
        return Result(instanceId, intakeId, (await registry.GetAsync(instanceId, intakeId, cancellationToken)).Inspect());
    });

    [McpServerTool(Name = "kicad_design_native_intake_list", ReadOnly = true),
     Description("List this service's native recovery observers for one explicit instance, including starting and paused observers. Recovers the intake ID after a lost start reply. An empty list says nothing about editor liveness. Does not discover observers in another or previous MCP process.")]
    public Task<CallToolResult> List(string instanceId, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Data(new { instanceId, sessions = registry.List(instanceId).Select(s => new
        { intakeId = s.IntakeId, sequence = s.Status.Sequence, phase = s.Status.Phase.ToString(),
            errorCode = s.Status.ErrorCode, errorMessage = s.Status.ErrorMessage }), liveMutationAuthorized = false }));
    });

    [McpServerTool(Name = "kicad_design_native_intake_wait", ReadOnly = true),
     Description("Inspect native intake status immediately, or supply afterSequence to wait for a newer status. Returns latest state, not an event history. Cancelling this wait does not stop observation. Paused persistence failures require repair and explicit resume; changed native identity or a silent stream requires a newly verified intake. Does not mutate designs.")]
    public Task<CallToolResult> Wait(string instanceId, string intakeId, CancellationToken cancellationToken,
        ulong? afterSequence = null) => Execute(async () =>
    {
        var session = await registry.GetAsync(instanceId, intakeId, cancellationToken);
        return Result(instanceId, intakeId, afterSequence is { } cursor ? await session.WaitAsync(cursor, cancellationToken) : session.Inspect());
    });

    [McpServerTool(Name = "kicad_design_native_intake_resume"),
     Description("Retry exactly the current paused native observation after repairing recovery persistence or reconciling a pending operation. Requires the latest sequence. Stale/non-paused requests fail. Identity changes and silent streams cannot be resumed: stop, verify reattachment, and start a new intake. Does not discard saved state or edit KiCad.")]
    public Task<CallToolResult> Resume(string instanceId, string intakeId, ulong expectedSequence, CancellationToken cancellationToken) => Execute(async () =>
    {
        var session = await registry.GetAsync(instanceId, intakeId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); session.Resume(expectedSequence);
        return Result(instanceId, intakeId, session.Inspect());
    });

    [McpServerTool(Name = "kicad_design_native_intake_stop"),
     Description("Stop one exact native recovery observer and await cleanup before releasing ownership. No further recovery writes occur after success. Does not close any editor or discard dirty documents. Cancelling before dispatch stops nothing; once dispatched cleanup completes. MCP shutdown also stops only its observers, never the native sessions.")]
    public Task<CallToolResult> Stop(string instanceId, string intakeId, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Result(instanceId, intakeId, await registry.StopAsync(instanceId, intakeId));
    });

    private static CallToolResult Result(string instanceId, string intakeId, DesignNativeIntakeStatus status) => Data(new
    {
        instanceId, intakeId, sequence = status.Sequence, phase = status.Phase.ToString(),
        observation = status.Observation is not { } observation ? null : new
        {
            reason = observation.Reason.ToString(), recoveryRevisionToken = observation.RecoveryRevisionToken,
            nativeStateChanged = observation.NativeStateChanged, choicesInvalidated = observation.ChoicesInvalidated,
            reattachRequired = observation.ReattachRequired,
            delivery = observation.Delivery is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(
                SchematicJson.Formatter.Format(observation.Delivery.Event)),
            errorCode = observation.ErrorCode, errorMessage = observation.ErrorMessage
        },
        errorCode = status.ErrorCode, errorMessage = status.ErrorMessage, liveMutationAuthorized = false
    });

    private static CallToolResult Data(object value, bool error = false)
    {
        var data = JsonSerializer.SerializeToElement(value);
        return new() { IsError = error, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return Data(new { errorCode = error switch { AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status, _ => "native_intake_unavailable" },
                errorMessage = error.Message }, true);
        }
    }
}
