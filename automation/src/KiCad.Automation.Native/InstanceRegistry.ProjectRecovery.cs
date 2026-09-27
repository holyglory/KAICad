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
            var record = connections.TryGetValue(instanceId, out var connection)
                ? connection.Record : await ReadSavedAsync(instanceId, token);
            if (startingProjects.Contains(record.ProjectPath)
                || await ProvenExitAsync(instanceId, record.Epoch, token) is null)
                throw new AutomationException("project_owned",
                    "Close this project's KiCad process before restoring its missing project file.");
            foreach (var other in connections.Values.Where(c => c.Record.ProjectPath == record.ProjectPath
                         && c.Record.InstanceId != instanceId))
                if (await ProvenExitAsync(other.Record.InstanceId, other.Record.Epoch, token) is null)
                    throw new AutomationException("project_owned", "Another KiCad process owns this project.");
            token.ThrowIfCancellationRequested();
            return action(record);
        }
        finally { changes.Release(); }
    }
}
