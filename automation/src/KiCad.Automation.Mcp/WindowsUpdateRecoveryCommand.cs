using System.Text.Json;

namespace KiCad.Automation.Mcp;

public static class WindowsUpdateRecoveryCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args.Length != 7 || args[0] != "--recover-update" || args[1] != "--installation" || args[3] != "--operation"
                || args[5] != "--attempt" || !Guid.TryParseExact(args[4], "D", out var operation) || operation == Guid.Empty
                || !Guid.TryParseExact(args[6], "D", out var attempt) || attempt == Guid.Empty)
                throw new ArgumentException("Use --recover-update --installation ABSOLUTE_ROOT --operation UUID --attempt UUID.");
            var result = await WindowsUpdateRecovery.RecoverAsync(args[2], operation, attempt, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, result }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return ExitCode(result);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException
            or UnauthorizedAccessException or PlatformNotSupportedException or OperationCanceledException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1,
                status = error is OperationCanceledException ? "cancelled" : "failed", nativeEditorRestarted = false,
                error = new { kind = error.GetType().Name, message = error.Message } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return error is OperationCanceledException ? 2 : 1;
        }
        finally { await output.FlushAsync(CancellationToken.None); }
    }

    internal static int ExitCode(WindowsUpdateRecoveryResult result) =>
        result.NativeEditorRestarted || result.Status is "restored" or "original_running" or "attempt_already_recorded" ? 0
        : result.Status == "recovery_cancelled" ? 2
        : 1;
}
