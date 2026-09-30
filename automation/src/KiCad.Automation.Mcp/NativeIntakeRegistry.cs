using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>Process-local ownership of recovery observers, never editor ownership.</summary>
public sealed class NativeIntakeRegistry : IAsyncDisposable
{
    private sealed class Entry(string instanceId, string recoveryPath)
    {
        public string InstanceId { get; } = instanceId;
        public string RecoveryPath { get; } = recoveryPath;
        public CancellationTokenSource Starting { get; } = new();
        public TaskCompletionSource<DesignNativeIntakeSession?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<string, string, CancellationToken, Task<DesignNativeIntakeSession>> start;
    private bool disposed;

    public NativeIntakeRegistry(InstanceRegistry instances) : this(async (instanceId, path, token) =>
    {
        var store = new DesignRecoveryStore(path);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "Create recovery state before observing native edits.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to another instance.");
        return await DesignNativeIntakeSession.CreateAsync(store, instances.Client(instanceId), token);
    }) { }

    internal NativeIntakeRegistry(Func<string, string, CancellationToken, Task<DesignNativeIntakeSession>> start) => this.start = start;

    public async Task<string> StartAsync(string instanceId, string recoveryPath, CancellationToken token)
    {
        ValidateInstance(instanceId); token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_native_intake_target", "Provide an absolute recovery path.");
        recoveryPath = Path.GetFullPath(recoveryPath);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string id = Guid.NewGuid().ToString("D");
        var entry = new Entry(instanceId, recoveryPath);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.Values.Any(e => string.Equals(e.RecoveryPath, recoveryPath, comparison)))
                throw new AutomationException("native_intake_ownership_conflict", "Stop the existing native intake for this recovery record first.");
            entries.Add(id, entry);
        }
        // Reserve ownership before asynchronous discovery; independent targets
        // can start concurrently, and list recovers IDs after a lost reply.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Starting.Token);
        try
        {
            var session = await start(instanceId, recoveryPath, linked.Token);
            entry.Ready.SetResult(session);
            return id;
        }
        catch
        {
            entry.Ready.TrySetResult(null);
            lock (gate) entries.Remove(id);
            throw;
        }
    }

    private Entry GetEntry(string instanceId, string intakeId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(intakeId, out var entry) || entry.InstanceId != instanceId)
                throw new AutomationException("unknown_native_intake", "No native intake matches both explicit IDs.");
            return entry;
        }
    }

    public async Task<DesignNativeIntakeSession> GetAsync(string instanceId, string intakeId, CancellationToken token) =>
        await GetEntry(instanceId, intakeId).Ready.Task.WaitAsync(token)
            ?? throw new AutomationException("native_intake_start_failed", "The native intake did not start; inspect the start error and reattach if necessary.");

    public IReadOnlyList<(string IntakeId, DesignNativeIntakeStatus Status)> List(string instanceId)
    {
        ValidateInstance(instanceId);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return entries.Where(pair => pair.Value.InstanceId == instanceId).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value.Ready.Task.IsCompletedSuccessfully && pair.Value.Ready.Task.Result is { } session
                    ? session.Inspect() : new DesignNativeIntakeStatus(0, DesignNativeIntakePhase.Starting, null, null, null))).ToArray();
        }
    }

    public async Task<DesignNativeIntakeStatus> StopAsync(string instanceId, string intakeId)
    {
        var entry = GetEntry(instanceId, intakeId);
        entry.Starting.Cancel();
        var session = await entry.Ready.Task;
        if (session is not null) await session.DisposeAsync();
        lock (gate) entries.Remove(intakeId);
        return session?.Inspect() ?? new(0, DesignNativeIntakePhase.Stopped, null, null, null);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] owned;
        lock (gate) { disposed = true; owned = entries.Values.ToArray(); }
        foreach (var entry in owned) entry.Starting.Cancel();
        await Task.WhenAll(owned.Select(async entry =>
        {
            var session = await entry.Ready.Task;
            if (session is not null) await session.DisposeAsync();
        }));
        lock (gate) entries.Clear();
    }

    private static void ValidateInstance(string instanceId)
    {
        if (!Guid.TryParseExact(instanceId, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_native_intake_target", "Provide an explicit instance ID.");
    }
}
