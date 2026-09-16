using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>Process-local ownership of automatic synchronization workers. The
/// reservation is made before native discovery, so competing starts cannot race.
/// It never owns or closes the native editor itself.</summary>
public sealed class AutomaticDesignRegistry : IAsyncDisposable
{
    private sealed class Entry(string instanceId, string recoveryPath, string designPath)
    {
        public string InstanceId { get; } = instanceId;
        public string RecoveryPath { get; } = recoveryPath;
        public string DesignPath { get; } = designPath;
        public CancellationTokenSource Starting { get; } = new();
        public TaskCompletionSource<AutomaticDesignSynchronization?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<string, string, string, string, CancellationToken, Task<AutomaticDesignSynchronization>> start;
    private bool disposed;
    public AutomaticDesignRegistry(InstanceRegistry instances) : this(async (instanceId, recovery, design, revision, token) =>
        await AutomaticDesignSynchronization.StartAsync(new DesignRecoveryStore(recovery), instances.Client(instanceId), design, revision, token)) { }
    internal AutomaticDesignRegistry(Func<string, string, string, string, CancellationToken, Task<AutomaticDesignSynchronization>> start) => this.start = start;

    public async Task<string> StartAsync(string instanceId, string recoveryPath, string designPath, string expectedRecoveryRevision, CancellationToken token)
    {
        ValidateInstance(instanceId); token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath) || !Path.IsPathFullyQualified(designPath)) throw new AutomationException("invalid_automatic_sync_target", "Provide absolute recovery and design paths.");
        recoveryPath = Path.GetFullPath(recoveryPath); designPath = Path.GetFullPath(designPath);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(recoveryPath, designPath, comparison)) throw new AutomationException("invalid_automatic_sync_target", "Recovery and design files must be separate.");
        string id = Guid.NewGuid().ToString("D"); var entry = new Entry(instanceId, recoveryPath, designPath);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.Values.Any(e => string.Equals(e.RecoveryPath, recoveryPath, comparison) || string.Equals(e.DesignPath, designPath, comparison)))
                throw new AutomationException("automatic_sync_ownership_conflict", "Another automatic synchronization owns this design or recovery record.");
            entries.Add(id, entry);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Starting.Token);
        try { var session = await start(instanceId, recoveryPath, designPath, expectedRecoveryRevision, linked.Token); entry.Ready.SetResult(session); return id; }
        catch { entry.Ready.TrySetResult(null); lock (gate) entries.Remove(id); throw; }
    }
    private Entry GetEntry(string instanceId, string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(id, out var entry) || entry.InstanceId != instanceId) throw new AutomationException("unknown_automatic_sync", "No automatic synchronization matches both explicit IDs.");
            return entry;
        }
    }
    private async Task<AutomaticDesignSynchronization> GetAsync(string instanceId, string id, CancellationToken token) =>
        await GetEntry(instanceId, id).Ready.Task.WaitAsync(token) ?? throw new AutomationException("automatic_sync_start_failed", "The automatic synchronization did not start.");
    public AutomaticDesignStatus Inspect(string instanceId, string id)
    {
        var entry = GetEntry(instanceId, id);
        return entry.Ready.Task.IsCompletedSuccessfully && entry.Ready.Task.Result is { } session ? session.Inspect() : new(0, AutomaticDesignPhase.Starting, null, null, false, null, null);
    }
    public async Task<AutomaticDesignStatus> WaitAsync(string instanceId, string id, ulong afterSequence, CancellationToken token) => await (await GetAsync(instanceId, id, token)).WaitAsync(afterSequence, token);
    public void Resume(string instanceId, string id, ulong expectedSequence)
    {
        var entry = GetEntry(instanceId, id);
        if (!entry.Ready.Task.IsCompletedSuccessfully || entry.Ready.Task.Result is not { } session) throw new AutomationException("automatic_sync_starting", "Wait for automatic synchronization discovery before resuming.");
        session.Resume(expectedSequence);
    }
    public async Task<AutomaticDesignStatus> StopAsync(string instanceId, string id)
    {
        var entry = GetEntry(instanceId, id); entry.Starting.Cancel(); var session = await entry.Ready.Task;
        if (session is not null) await session.DisposeAsync(); lock (gate) entries.Remove(id);
        return session?.Inspect() ?? new(0, AutomaticDesignPhase.Stopped, null, null, false, null, null);
    }
    public IReadOnlyList<(string SessionId, AutomaticDesignStatus Status)> List(string instanceId)
    {
        ValidateInstance(instanceId); lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return entries.Where(e => e.Value.InstanceId == instanceId).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e =>
                (e.Key, e.Value.Ready.Task.IsCompletedSuccessfully && e.Value.Ready.Task.Result is { } session ? session.Inspect() : new AutomaticDesignStatus(0, AutomaticDesignPhase.Starting, null, null, false, null, null))).ToArray();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Entry[] owned; lock (gate) { disposed = true; owned = entries.Values.ToArray(); }
        foreach (var entry in owned) entry.Starting.Cancel();
        await Task.WhenAll(owned.Select(async entry => { var session = await entry.Ready.Task; if (session is not null) await session.DisposeAsync(); }));
        lock (gate) entries.Clear();
    }
    private static void ValidateInstance(string instanceId)
    { if (!Guid.TryParseExact(instanceId, "D", out var id) || id == Guid.Empty) throw new AutomationException("invalid_automatic_sync_target", "Provide an explicit instance ID."); }
}
