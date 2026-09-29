using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed partial class InstanceRegistry
{
    // SA-01/SA-04: keep the registry's established instance/project ownership while creating an absent
    // container. XML must not select an unrelated destination, and a running writer retains ownership.
    internal async Task<T> WithStoppedProjectAsync<T>(string instanceId, Func<InstanceRecord, T> action,
        CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            using var lease = await MetadataLease(token);
            var record = await RequireStoppedProjectAsync(instanceId, token);
            return action(record);
        }
        finally { changes.Release(); }
    }

    // SA-04: the recovery record names an exact instance. Never choose another saved
    // registration for its project between creating the container and starting KiCad.
    internal async Task<InstanceRegistration> StartProjectRecoveryAsync(string executable, string instanceId,
        Action<InstanceRecord> prepareProject, CancellationToken cancellationToken = default,
        bool? softwareRendering = null)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
            throw new AutomationException("missing_executable", "The native KiCad executable does not exist.");

        InstanceRecord record;
        UnverifiedInstanceLaunch launch;
        await changes.WaitAsync(cancellationToken);
        try
        {
            using var lease = await MetadataLease(cancellationToken);
            record = await RequireStoppedProjectAsync(instanceId, cancellationToken);
            launch = await ReserveLaunchAsync(record.ProjectPath, record, cancellationToken);
            startingProjects.Add(record.ProjectPath);
            try
            {
                prepareProject(record);
                if (!File.Exists(record.ProjectPath) || Path.GetExtension(record.ProjectPath) != ".kicad_pro")
                    throw new AutomationException("invalid_project", "An existing .kicad_pro file is required.");
            }
            catch
            {
                try { await RetireUnstartedLaunchAsync(launch); }
                finally { startingProjects.Remove(record.ProjectPath); }
                throw;
            }
        }
        finally { changes.Release(); }
        return await StartReservedInstanceAsync(executable, launch, record, cancellationToken, softwareRendering);
    }

    // Called only while changes is held. Both recovery entry points use the same
    // exact process-exit and competing-writer checks before touching project files.
    private async Task<InstanceRecord> RequireStoppedProjectAsync(string instanceId, CancellationToken token)
    {
        var record = connections.TryGetValue(instanceId, out var connection)
            ? connection.Record : await ReadSavedAsync(instanceId, token);
        await RequireCurrentSavedRecordAsync(record, token);
        await RequireNoPendingLaunchAsync(record.ProjectPath, token);
        if (startingProjects.Contains(record.ProjectPath)
            || await ProvenExitAsync(instanceId, record.Epoch, token) is null)
            throw new AutomationException("project_owned",
                "Close this project's KiCad process before restoring its missing project file.");
        foreach (var other in connections.Values.Where(c => c.Record.ProjectPath == record.ProjectPath
                     && c.Record.InstanceId != instanceId))
            if (await ProvenExitAsync(other.Record.InstanceId, other.Record.Epoch, token) is null)
                throw new AutomationException("project_owned", "Another KiCad process owns this project.");
        var saved = await SavedForProjectAsync(record.ProjectPath, token);
        if (saved.Any(r => r.InstanceId != instanceId && r.ProcessId is { } pid && r.ProcessStart is { } identity
                && ProcessIdentity.Runs(pid, identity) && ProcessIdentity.RunsInstance(pid, r.InstanceId)))
            throw new AutomationException("project_owned", "Another saved KiCad process owns this project; reattach it before recovery.");
        token.ThrowIfCancellationRequested();
        return record;
    }
}
