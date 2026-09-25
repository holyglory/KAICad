using System.ComponentModel;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record InstanceReconnectionView(string InstanceId, string ProjectPath, string Epoch,
    string EventEpoch, bool Reused, bool FreshSnapshotRequired = true);

[McpServerToolType]
public sealed class InstanceUpdateTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_instance_reconnect_after_update", UseStructuredContent = true),
     Description("Reconnect one saved instance after an explicit verified application update or recovery. Requires its original epoch, the absolute managed installation root and exact handoff operation UUID. Verifies old-process exit, signed retained payloads, original native-session proof and the live replacement before changing the connection. Legacy journals without origin proof are refused. Never starts, closes or signals an editor. Retry the same identities if a reply is lost. Returns the new process/event epochs; take fresh document snapshots before editing or resuming events. Does not itself synchronize XML."),
     KiCadCapability("service", "compiled-mcp plus native-api", "saved instance ID, original process epoch, installation root, handoff operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.McpReconnectsToTheVerifiedNativeReplacementAndRejectsOldEvents", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> Reconnect(string instanceId, string installationRoot, string operationId,
        string expectedOldEpoch, CancellationToken cancellationToken)
    {
        try
        {
            if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty)
                throw new AutomationException("invalid_operation", "An exact handoff operation UUID is required.");
            var adopted = await InstanceUpdateReconnection.ReconnectAsync(registry, instanceId, installationRoot,
                operation, expectedOldEpoch, cancellationToken);
            var session = await registry.Client(instanceId).HandshakeAsync(cancellationToken);
            if (session.Epoch != adopted.Instance.Epoch || session.ProjectPath != adopted.Instance.ProjectPath)
                throw new AutomationException("instance_changed", "The connection changed again while its result was being observed.");
            return Result(new InstanceReconnectionView(instanceId, session.ProjectPath, session.Epoch, session.EventEpoch, adopted.Reused), false);
        }
        catch (Exception error) when (error is AutomationException or IOException or ArgumentException or JsonException or PlatformNotSupportedException)
        {
            return Result(new { code = error is AutomationException automation ? automation.Code : error.GetType().Name,
                message = error.Message }, true);
        }
    }

    private static CallToolResult Result(object result, bool error)
    {
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
