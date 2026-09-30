using System.Text.Json;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// An observed launch attempt, never proof of a live or ready native process.
public sealed record UnverifiedInstanceLaunch(string InstanceId, string ProjectPath,
    string Endpoint, int? ProcessId, DateTimeOffset RequestedAt, string? ReplacesEpoch = null,
    ProcessStartIdentity? ProcessStart = null);

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
            if (File.Exists(Path.Combine(directory, id + ".json")))
            {
                var saved = await ReadSavedAsync(id, token);
                var replacement = await ReadLaunchAsync(id, token);
                // An old unmarked receipt never overrides a verified session. A marked
                // replacement stays discoverable until the old epoch has been replaced.
                if (replacement.ReplacesEpoch == saved.Epoch && replacement.ProjectPath == saved.ProjectPath)
                    result.Add(replacement);
            }
            else result.Add(await ReadLaunchAsync(id, token));
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
                || launch.ProcessId is <= 0 || launch.ReplacesEpoch is { Length: 0 }
                || (launch.ProcessStart is { } identity && (launch.ProcessId is null || string.IsNullOrWhiteSpace(identity.MachineId)
                    || string.IsNullOrWhiteSpace(identity.BootId) || identity.PidNamespace == 0))
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

    // Admission and the intent write share the existing cross-process metadata lease.
    // A receipt is not proof of a live process, but must be reconciled before its project,
    // socket and logs can be used by another launch (n9d9cb697ed628e53).
    private async Task RequireNoPendingLaunchAsync(string projectPath, CancellationToken token)
    {
        string launches = Path.Combine(directory, "launches");
        if (!Directory.Exists(launches)) return;
        foreach (string path in Directory.EnumerateFiles(launches, "*.json"))
        {
            var pending = await ReadLaunchAsync(Path.GetFileNameWithoutExtension(path), token);
            if (pending.ProjectPath != projectPath) continue;
            if (pending.ProcessStart is { } identity && ProcessIdentity.Ended(pending.ProcessId!.Value, identity))
            {
                await RetireExactLaunchAsync(pending);
                continue;
            }
            if (File.Exists(Path.Combine(directory, pending.InstanceId + ".json")))
            {
                var saved = await ReadSavedAsync(pending.InstanceId, token);
                // A successful verification can leave its older receipt when retirement
                // failed. A newer unmarked receipt remains unresolved even though discovery
                // cannot use it to bypass the saved epoch.
                if (saved.ProjectPath == pending.ProjectPath && saved.VerifiedAt >= pending.RequestedAt
                    && pending.ReplacesEpoch != saved.Epoch) continue;
            }
            throw new AutomationException("project_owned", $"KiCad instance {pending.InstanceId} has an unresolved startup for this project. "
                + $"Use kicad_instance_reattach for that instance at {pending.Endpoint}; its launch, process and logs were preserved.");
        }
    }

    private async Task RequireCurrentSavedRecordAsync(InstanceRecord record, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(directory, record.InstanceId + ".json"))) return;
        var saved = await ReadSavedAsync(record.InstanceId, token);
        if (saved.Epoch != record.Epoch || saved.ProjectPath != record.ProjectPath || saved.Endpoint != record.Endpoint)
            throw new AutomationException("instance_changed", "The saved instance changed in another server; reattach it before starting recovery.");
    }

    private async Task<UnverifiedInstanceLaunch> ReserveLaunchAsync(string projectPath, InstanceRecord? replacing,
        CancellationToken token)
    {
        string id = replacing?.InstanceId ?? Guid.NewGuid().ToString("D");
        string endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(id), "api.sock"));
        var launch = new UnverifiedInstanceLaunch(id, projectPath, endpoint, null, DateTimeOffset.UtcNow, replacing?.Epoch);
        await SaveLaunchAsync(launch, token);
        return launch;
    }

    // Only an intent this attempt still owns, before Process.Start, can be retired on failure.
    // Comparing the full receipt preserves a different attempt, even with the same instance ID.
    private async Task RetireUnstartedLaunchAsync(UnverifiedInstanceLaunch launch)
    {
        if (launch.ProcessId is not null || !File.Exists(LaunchPath(launch.InstanceId))) return;
        if (await ReadLaunchAsync(launch.InstanceId, CancellationToken.None) == launch)
            File.Delete(LaunchPath(launch.InstanceId));
    }

    // Called under the existing metadata lease. The launch's request identity is
    // compared before deletion so an unrelated retry cannot be removed.
    private async Task RetireExactLaunchAsync(UnverifiedInstanceLaunch launch)
    {
        if (!File.Exists(LaunchPath(launch.InstanceId))) return;
        var current = await ReadLaunchAsync(launch.InstanceId, CancellationToken.None);
        if (current == launch) File.Delete(LaunchPath(launch.InstanceId));
    }

    private static bool SameLaunchAttempt(UnverifiedInstanceLaunch first, UnverifiedInstanceLaunch second) =>
        first.InstanceId == second.InstanceId && first.ProjectPath == second.ProjectPath
        && first.Endpoint == second.Endpoint && first.RequestedAt == second.RequestedAt
        && first.ReplacesEpoch == second.ReplacesEpoch;

    // Called under the existing metadata lease after this server observed its exact child exit.
    internal async Task RetireExitedLaunchAsync(UnverifiedInstanceLaunch attempt, int processId)
    {
        try
        {
            if (!File.Exists(LaunchPath(attempt.InstanceId))) return;
            var current = await ReadLaunchAsync(attempt.InstanceId, CancellationToken.None);
            if (SameLaunchAttempt(current, attempt) && current.ProcessId == processId)
                File.Delete(LaunchPath(attempt.InstanceId));
        }
        catch (AutomationException)
        {
            // A malformed or competing receipt is preserved for explicit reconciliation;
            // it must not mask the definitive native start failure.
        }
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

    private async Task RetireLaunchAsync(InstanceRecord verified, bool metadataLeaseHeld = false)
    {
        // A verified record was committed first. A leftover receipt is harmless
        // and omitted from discovery; cleanup must not undo a successful attach or
        // remove a later attempt already replacing this newly verified epoch.
        try
        {
            using var lease = metadataLeaseHeld ? null : await MetadataLease(CancellationToken.None);
            if (!File.Exists(LaunchPath(verified.InstanceId))) return;
            var launch = await ReadLaunchAsync(verified.InstanceId, CancellationToken.None);
            if (launch.ProjectPath == verified.ProjectPath && launch.RequestedAt <= verified.VerifiedAt
                && launch.ReplacesEpoch != verified.Epoch)
                File.Delete(LaunchPath(verified.InstanceId));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (AutomationException) { } // A verified registration must survive stale or malformed cleanup metadata.
    }
}
