using System.Text.Json;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Managed identity checks for the process-local PCB artwork receipt cache.
/// A cache hit is evidence only for the instance and native process epoch that
/// produced it; the caller's supplied epoch cannot replace the stored one.
/// </summary>
internal static class PcbArtworkContract
{
    internal static void ValidateReceiptIdentity(
        JsonElement payload, string expectedInstanceId, string expectedProcessEpoch)
    {
        if (!payload.TryGetProperty("instanceId", out JsonElement instance)
            || instance.ValueKind != JsonValueKind.String
            || !string.Equals(instance.GetString(), expectedInstanceId, StringComparison.Ordinal))
            throw new AutomationException("wrong_instance_receipt",
                "The artwork receipt belongs to another MCP instance or is missing its instance identity.");

        if (!payload.TryGetProperty("processEpoch", out JsonElement epoch)
            || epoch.ValueKind != JsonValueKind.String
            || !string.Equals(epoch.GetString(), expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("stale_process_epoch",
                "The artwork receipt belongs to another native process epoch or is missing its epoch identity.");
    }
}
