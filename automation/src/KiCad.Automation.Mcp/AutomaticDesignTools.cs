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
    [System.ComponentModel.Description("Start the automatic synchronization worker for one attached KiCad instance, its recovery record (absolute path, current revision token) and design XML (absolute path). It applies saved XML changes to KiCad and publishes KiCad's saved edits to the XML, one at a time, and pauses with an error code instead of guessing: conflicting edits on both sides keep both versions, and new symbols whose design owner exact identities cannot decide return resolution requests (kicad_design_sync_plan shows them). Optional blockGraphPath (absolute) and designId (the design's identity in the hardware repository) keep block ownership: after each synchronization, components no block of the selected design owns, such as symbols placed in KiCad, are bound to the block of the sheet they sit on, when one block owns every other component on that sheet, as new block revisions; a component whose block cannot be decided (a sheet no block or several blocks own) pauses the worker with block_owner_resolution_required until it is bound with kicad_diagram_components_set and the worker is resumed.")]
    public Task<CallToolResult> Start(string instanceId, string recoveryPath, string designPath,
        string expectedRecoveryRevision, CancellationToken token, string? blockGraphPath = null, string? designId = null) => Execute(async () =>
    {
        BlockOwnershipTarget? blocks = null;
        if (blockGraphPath is not null || designId is not null)
        {
            if (blockGraphPath is null || !Guid.TryParseExact(designId, "D", out var design) || design == Guid.Empty)
                throw new AutomationException("invalid_automatic_sync_target", "Provide blockGraphPath and designId together; designId is the design's exact identity.");
            blocks = new(blockGraphPath, design);
        }
        string id = await registry.StartAsync(instanceId, recoveryPath, designPath, expectedRecoveryRevision, blocks, token);
        return Data(new { instanceId, sessionId = id, status = Describe(registry.Inspect(instanceId, id)), blockOwnership = blocks is not null,
            liveMutationAuthorized = false });
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
