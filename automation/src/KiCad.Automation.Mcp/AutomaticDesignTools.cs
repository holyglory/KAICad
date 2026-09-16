using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class AutomaticDesignTools(AutomaticDesignRegistry registry)
{
    [McpServerTool(Name = "kicad_design_automatic_sync_start")]
    public Task<CallToolResult> Start(string instanceId, string recoveryPath, string designPath,
        string expectedRecoveryRevision, CancellationToken token) => Execute(async () =>
    {
        string id = await registry.StartAsync(instanceId, recoveryPath, designPath, expectedRecoveryRevision, token);
        return Data(new { instanceId, sessionId = id, status = Describe(registry.Inspect(instanceId, id)), liveMutationAuthorized = false });
    });

    [McpServerTool(Name = "kicad_design_automatic_sync_list", ReadOnly = true)]
    public Task<CallToolResult> List(string instanceId, CancellationToken token) => Execute(() =>
    {
        token.ThrowIfCancellationRequested(); return Task.FromResult(Data(new { instanceId,
            sessions = registry.List(instanceId).Select(s => new { sessionId = s.SessionId, status = Describe(s.Status) }), liveMutationAuthorized = false }));
    });

    [McpServerTool(Name = "kicad_design_automatic_sync_wait", ReadOnly = true)]
    public Task<CallToolResult> Wait(string instanceId, string sessionId, ulong afterSequence, CancellationToken token) => Execute(async () =>
        Data(new { instanceId, sessionId, status = Describe(await registry.WaitAsync(instanceId, sessionId, afterSequence, token)), liveMutationAuthorized = false }));

    [McpServerTool(Name = "kicad_design_automatic_sync_resume")]
    public Task<CallToolResult> Resume(string instanceId, string sessionId, ulong expectedSequence, CancellationToken token) => Execute(() =>
    {
        token.ThrowIfCancellationRequested(); registry.Resume(instanceId, sessionId, expectedSequence);
        return Task.FromResult(Data(new { instanceId, sessionId, status = Describe(registry.Inspect(instanceId, sessionId)), liveMutationAuthorized = false }));
    });

    [McpServerTool(Name = "kicad_design_automatic_sync_stop")]
    public Task<CallToolResult> Stop(string instanceId, string sessionId, CancellationToken token) => Execute(async () =>
    {
        token.ThrowIfCancellationRequested(); return Data(new { instanceId, sessionId,
            status = Describe(await registry.StopAsync(instanceId, sessionId)), liveMutationAuthorized = false });
    });

    private static CallToolResult Data(object value, bool error = false)
    {
        var data = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }
    private static object Describe(AutomaticDesignStatus status) => new
    {
        sequence = status.Sequence, phase = status.Phase.ToString(), recoveryRevisionToken = status.RecoveryRevisionToken,
        operationId = status.OperationId, reattachRequired = status.ReattachRequired,
        errorCode = status.ErrorCode, errorMessage = status.ErrorMessage
    };
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "automatic_sync_unavailable", errorMessage = error.Message }, true); }
    }
}
