using System.Text.Json;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// An observed launch attempt, never proof of a live or ready native process.
public sealed record UnverifiedInstanceLaunch(string InstanceId, string ProjectPath,
    string Endpoint, int? ProcessId, DateTimeOffset RequestedAt);

public sealed partial class InstanceRegistry
{
    private string LaunchPath(string id) => Path.Combine(directory, "launches", id + ".json");

    public async Task<IReadOnlyList<InstanceRecord>> SavedSessionsAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<InstanceRecord>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            result.Add(await ReadSavedAsync(Path.GetFileNameWithoutExtension(file), token));
        }
        return result;
    }

    private async Task<InstanceRecord> ReadSavedAsync(string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty)
            throw new AutomationException("invalid_registry", "Invalid saved session identity.");
        try
        {
            var saved = JsonSerializer.Deserialize<InstanceRecord>(
                await File.ReadAllTextAsync(Path.Combine(directory, id + ".json"), token));
            if (saved is null || saved.InstanceId != id || string.IsNullOrWhiteSpace(saved.ProjectPath)
                || !Path.IsPathFullyQualified(saved.ProjectPath) || string.IsNullOrWhiteSpace(saved.Epoch)
                || saved.VerifiedAt == default || saved.ProcessId is <= 0 || string.IsNullOrWhiteSpace(saved.Endpoint)
                || (saved.ProcessStart is { } identity && (saved.ProcessId is null || string.IsNullOrWhiteSpace(identity.MachineId)
                    || string.IsNullOrWhiteSpace(identity.BootId) || identity.PidNamespace == 0)))
                throw new AutomationException("invalid_registry", "The saved session record is invalid.");
            NngTransport.ValidateEndpoint(saved.Endpoint);
            return saved;
        }
        catch (JsonException)
        { throw new AutomationException("invalid_registry", "The saved session record is invalid."); }
        catch (FileNotFoundException)
        { throw new AutomationException("unknown_instance", "The saved session record no longer exists."); }
        catch (DirectoryNotFoundException)
        { throw new AutomationException("unknown_instance", "The saved session record no longer exists."); }
    }

    public async Task<IReadOnlyList<UnverifiedInstanceLaunch>> PendingLaunchesAsync(CancellationToken token = default)
    {
        string launches = Path.Combine(directory, "launches");
        if (!Directory.Exists(launches)) return [];
        var result = new List<UnverifiedInstanceLaunch>();
        foreach (string file in Directory.EnumerateFiles(launches, "*.json").Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(file);
            if (File.Exists(Path.Combine(directory, id + ".json"))) continue;
            result.Add(await ReadLaunchAsync(id, token));
        }
        return result;
    }

    private async Task<UnverifiedInstanceLaunch> ReadLaunchAsync(string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty)
            throw new AutomationException("invalid_registry", "Invalid launch receipt identity.");
        try
        {
            var launch = JsonSerializer.Deserialize<UnverifiedInstanceLaunch>(await File.ReadAllTextAsync(LaunchPath(id), token));
            if (launch is null || launch.InstanceId != id || string.IsNullOrWhiteSpace(launch.ProjectPath)
                || !Path.IsPathFullyQualified(launch.ProjectPath) || launch.RequestedAt == default
                || launch.ProcessId is <= 0
                || launch.Endpoint != NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(id), "api.sock")))
                throw new AutomationException("invalid_registry", "The unverified launch receipt is invalid.");
            return launch;
        }
        catch (FileNotFoundException)
        { throw new AutomationException("unknown_instance", "No verified session or unverified launch receipt exists for this instance."); }
        catch (DirectoryNotFoundException)
        { throw new AutomationException("unknown_instance", "No verified session or unverified launch receipt exists for this instance."); }
        catch (JsonException)
        { throw new AutomationException("invalid_registry", "The unverified launch receipt is invalid."); }
    }

    private async Task SaveLaunchAsync(UnverifiedInstanceLaunch launch, CancellationToken token)
    {
        string destination = LaunchPath(launch.InstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(launch), token);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void RetireLaunch(string id)
    {
        // A verified record was committed first. A leftover receipt is harmless
        // and omitted from discovery; cleanup must not undo a successful attach.
        try { File.Delete(LaunchPath(id)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
