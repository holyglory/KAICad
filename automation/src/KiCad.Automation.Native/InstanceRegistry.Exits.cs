using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>How the KiCad process of one instance epoch ended, as this server proved it. Evidence
/// "exit-status": this server started the process and the operating system returned its exit
/// status to it. "process-absent": the process recorded for that epoch no longer exists, or its
/// process ID now belongs to a process started later, so its exit status is unknown. ExitCode is
/// the status .NET reports. It reports a process ended by a signal as 128 plus the signal number,
/// and Signal is the signal that status stands for; a program that itself exits with such a status
/// is reported the same way, so the signal is how the exit was reported, not a separate proof.</summary>
public sealed record InstanceExit(string InstanceId, string Epoch, int ProcessId, int? ExitCode, int? Signal,
    string Evidence, DateTimeOffset ObservedAt)
{
    public const string ExitStatusEvidence = "exit-status", ProcessAbsentEvidence = "process-absent";

    public string? SignalName => Signal switch
    {
        null => null, 1 => "SIGHUP", 2 => "SIGINT", 3 => "SIGQUIT", 4 => "SIGILL", 6 => "SIGABRT", 7 => "SIGBUS", 8 => "SIGFPE",
        9 => "SIGKILL", 11 => "SIGSEGV", 13 => "SIGPIPE", 14 => "SIGALRM", 15 => "SIGTERM",
        _ => "signal " + Signal.Value.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>One sentence part, for example "ended with exit status 137, the status reported for a
    /// process ended by signal 9 (SIGKILL)".</summary>
    public string Describe() => Evidence == ProcessAbsentEvidence
        ? "is no longer running (its process no longer exists, so its exit status is unknown)"
        : Signal is { } signal
            ? $"ended with exit status {ExitCode}, the status reported for a process ended by signal {signal} ({SignalName})"
            : $"ended with exit status {ExitCode}";

    internal static int? SignalOf(int exitCode) =>
        !OperatingSystem.IsWindows() && exitCode is > 128 and <= 128 + 64 ? exitCode - 128 : null;
}

/// <summary>Which Linux process an instance record names: the machine it ran on (/etc/machine-id),
/// the boot it ran in, the process ID namespace of the server that recorded it (the inode of
/// /proc/self/ns/pid) and the kernel start time of the process. A later server proves that exact
/// process ended only on the same machine: in the same namespace, or after that machine restarted.
/// A record written on another computer (for example one sharing this home folder) proves nothing
/// here, so its different boot is never taken for a restart, and a process ID read in another
/// namespace or reused by another program is never taken for it.</summary>
public sealed record ProcessStartIdentity(string MachineId, string BootId, ulong PidNamespace, ulong StartTicks);

/// <summary>What this server can prove about the process behind an attached instance, without
/// contacting KiCad. Only a proven exit ever changes what the registry reports.</summary>
internal abstract class InstanceProcessObserver(string instanceId, string epoch, int processId,
    Func<InstanceExit, Task>? recorder)
{
    private readonly TaskCompletionSource<InstanceExit> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InstanceExit? exit;
    public string InstanceId { get; } = instanceId;
    public string Epoch { get; } = epoch;
    public int ProcessId { get; } = processId;
    public InstanceExit? KnownExit => Volatile.Read(ref exit);
    /// <summary>Completes only once the exit is proven; never for a process that keeps running.</summary>
    public Task<InstanceExit> Exited => exited.Task;
    /// <summary>A local check (no request to KiCad) that records and returns a proven exit.</summary>
    public abstract InstanceExit? Probe();
    /// <summary>True when this server positively observes the same process still running.</summary>
    public abstract bool VerifiedRunning { get; }
    /// <summary>Completes once the exit is proven, and is cancelled by the token otherwise. A request
    /// waiting on KiCad waits on this too, so it fails soon after KiCad ends instead of after its
    /// reply timeout.</summary>
    public virtual Task<InstanceExit> WaitForExitAsync(CancellationToken token) => Exited.WaitAsync(token);

    protected InstanceExit Prove(InstanceExit value)
    {
        var first = Interlocked.CompareExchange(ref exit, value, null) ?? value;
        if (ReferenceEquals(first, value) && exited.TrySetResult(value) && recorder is { } record)
            _ = PersistAsync(record, value);
        return first;
    }

    private static async Task PersistAsync(Func<InstanceExit, Task> record, InstanceExit value)
    {
        // The exit is already known in memory; the durable copy lets a restarted server and the
        // release of a pending operation prove it later. A failed write loses only that copy.
        try { await record(value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public AutomationException ExitedError(InstanceExit value, bool requestMayHaveReached)
    {
        var message = new StringBuilder($"KiCad instance {InstanceId} (process {value.ProcessId}) {value.Describe()}");
        message.Append(" at ").Append(value.ObservedAt.ToString("O", CultureInfo.InvariantCulture)).Append('.');
        message.Append(" It cannot answer this or any later request, and changes it had not saved are gone.");
        if (requestMayHaveReached)
            message.Append(" This request may have reached KiCad before it ended; only what KiCad had written to disk remains of it.");
        message.Append(" Start KiCad again for its project with kicad_instance_start (or attach a KiCad started again with this instance ID),")
            .Append(" which continues this instance ID with a new process; a synchronization it left pending stays recorded in its recovery record.");
        return new AutomationException("instance_exited", message.ToString());
    }
}

/// <summary>A KiCad process this server started: the operating system reports its exit status.</summary>
internal sealed class ChildProcessObserver : InstanceProcessObserver
{
    public ChildProcessObserver(string instanceId, string epoch, Process process, Func<InstanceExit, Task>? recorder)
        : base(instanceId, epoch, process.Id, recorder) => _ = WatchAsync(process);

    private async Task WatchAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            int code = process.ExitCode;
            Prove(new(InstanceId, Epoch, ProcessId, code, InstanceExit.SignalOf(code), InstanceExit.ExitStatusEvidence, DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); } // Disposing never signals KiCad.
    }

    public override InstanceExit? Probe() => KnownExit;
    public override bool VerifiedRunning => KnownExit is null;
}

/// <summary>A KiCad process this server did not start, identified on Linux by its process ID and the
/// kernel start time read when it was attached, so a reused process ID is never mistaken for the same
/// process. The operating system tells only a parent when a process ends, so its exit is proven by
/// checking /proc: whenever something asks, and every half second at most while a request waits on it.</summary>
internal sealed class ObservedProcessObserver(string instanceId, string epoch, int processId, ulong startTicks,
    Func<InstanceExit, Task>? recorder) : InstanceProcessObserver(instanceId, epoch, processId, recorder)
{
    public override InstanceExit? Probe()
    {
        if (KnownExit is { } known) return known;
        return ProcessIdentity.Gone(ProcessId, startTicks)
            ? Prove(new(InstanceId, Epoch, ProcessId, null, null, InstanceExit.ProcessAbsentEvidence, DateTimeOffset.UtcNow))
            : null;
    }

    public override bool VerifiedRunning => Probe() is null;

    // One bounded-backoff check per waiting request, from 50 ms up to 500 ms between reads of one
    // small /proc file; it stops when the request completes (the token) or the exit is proven.
    public override async Task<InstanceExit> WaitForExitAsync(CancellationToken token)
    {
        int delay = 50;
        while (true)
        {
            if (Probe() is { } exit) return exit;
            await Task.Delay(delay, token);
            delay = Math.Min(delay * 2, 500);
        }
    }
}

internal static class ProcessIdentity
{
    /// <summary>True when the process with this ID runs KiCad for exactly this automation instance: on
    /// Linux its command line carries "--automation" with the instance ID. A process ID reported from
    /// another PID namespace (for example a sandboxed KiCad) or reused by another program never
    /// matches, so it is never used to prove anything. Other platforms cannot check it cheaply.</summary>
    public static bool RunsInstance(int processId, string instanceId)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0) return false;
        string[] arguments;
        try { arguments = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/cmdline").Split('\0'); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        for (int index = 0; index + 1 < arguments.Length; index++)
            if (arguments[index] == "--automation" && arguments[index + 1] == instanceId) return true;
        return false;
    }

    /// <summary>The kernel start counter of a running (not ended) Linux process, or null when the
    /// process does not exist, has ended, or the platform does not provide one.</summary>
    public static ulong? LinuxStartTicks(int processId)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0) return null;
        string stat;
        try { stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        int end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 >= stat.Length) return null;
        // An ended process that its parent has not collected yet ('Z') or that is being removed ('X') is gone.
        if (stat[end + 2] is 'Z' or 'X') return null;
        string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 20 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ticks)
            ? ticks : null;
    }

    /// <summary>True only when the process this server observed (in its own process ID namespace) is
    /// proven gone: its process ID no longer exists, the process has ended, or the ID now names a process
    /// started at another time. Linux only; elsewhere nothing is proven.</summary>
    public static bool Gone(int processId, ulong startTicks)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0) return false;
        if (!Directory.Exists($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}")) return true;
        ulong? now = LinuxStartTicks(processId);
        // Present but unreadable proves nothing; ended, or started at another time, proves the exit.
        return now is null ? IsEnded(processId) : now != startTicks;
    }

    /// <summary>The identity to record for a running process, or null when it has ended, does not
    /// exist, or the platform does not provide one (only Linux does), or this machine's identity or
    /// boot cannot be read: an identity that does not name its machine could never prove anything.</summary>
    public static ProcessStartIdentity? Record(int? processId)
    {
        if (processId is not { } pid || MachineId() is not { } machine || BootId() is not { } boot
            || PidNamespace() is not { } space || LinuxStartTicks(pid) is not { } ticks) return null;
        return new(machine, boot, space, ticks);
    }

    /// <summary>True when the process ID names exactly the recorded process, still running: same machine,
    /// same boot, same process ID namespace as this server, same start time. False when this machine's
    /// identity cannot be read or differs from the recorded one.</summary>
    public static bool Runs(int processId, ProcessStartIdentity identity) =>
        SameMachine(identity) && BootId() is { } boot && boot == identity.BootId && PidNamespace() == identity.PidNamespace
        && LinuxStartTicks(processId) == identity.StartTicks;

    /// <summary>True only when a recorded process, which this server may never have observed (for example
    /// one recorded before the server restarted), is proven to have ended on this machine: this machine
    /// restarted since it was recorded, or in the same process ID namespace its ID no longer names that
    /// process. A record from another machine, or when this machine's identity or boot cannot be read, or
    /// from another namespace of this boot, proves nothing: another computer's boot ID always differs, and
    /// is never taken for a restart.</summary>
    public static bool Ended(int processId, ProcessStartIdentity identity)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0 || !SameMachine(identity) || BootId() is not { } boot) return false;
        if (boot != identity.BootId) return true;
        return PidNamespace() == identity.PidNamespace && Gone(processId, identity.StartTicks);
    }

    private static bool SameMachine(ProcessStartIdentity identity) =>
        MachineId() is { } machine && string.Equals(machine, identity.MachineId, StringComparison.Ordinal);

    /// <summary>This machine's identity: /etc/machine-id, or /var/lib/dbus/machine-id where only that
    /// exists; null off Linux or when neither can be read.</summary>
    internal static string? MachineId()
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (string path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.ReadAllText(path).Trim() is { Length: > 0 } machine) return machine;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string? BootId()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() is { Length: > 0 } boot ? boot : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // The inode of this server's process ID namespace, from the link text "pid:[4026531836]".
    private static ulong? PidNamespace()
    {
        if (!OperatingSystem.IsLinux()) return null;
        string? target;
        try { target = new FileInfo("/proc/self/ns/pid").LinkTarget; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (target is null || !target.StartsWith("pid:[", StringComparison.Ordinal) || !target.EndsWith(']')) return null;
        return ulong.TryParse(target.AsSpan(5, target.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out ulong inode)
            && inode != 0 ? inode : null;
    }

    private static bool IsEnded(int processId)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
            int end = stat.LastIndexOf(')');
            return end >= 0 && end + 2 < stat.Length && stat[end + 2] is 'Z' or 'X';
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The exit of one KiCad process epoch as an instance registry proved it (InstanceRegistry.ProvenExitAsync):
/// this server's own observer of that process saw it end, or a saved registration of that epoch written on this
/// machine, in the same boot and process ID namespace, shows the process is gone. Nothing else creates one: the
/// constructor is private and ProveAsync returns one only when the registry proves the exit, so a caller cannot hand
/// the recovery store an exit it made up (DesignRecoveryStore.ReleaseExitedOperation takes only this type).</summary>
public sealed class ProvenInstanceExit
{
    private ProvenInstanceExit(InstanceExit exit) => Exit = exit;

    public InstanceExit Exit { get; }

    /// <summary>The registry's proof that the process serving <paramref name="epoch"/> of the instance ended, or null
    /// when the registry cannot prove it (the process may still run, possibly stopped, or cannot be observed).</summary>
    public static async Task<ProvenInstanceExit?> ProveAsync(InstanceRegistry registry, string instanceId, string epoch,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return await registry.ProvenExitAsync(instanceId, epoch, token) is { } exit ? new(exit) : null;
    }
}

public sealed record InstanceProcessStatus(InstanceRecord Instance, string State, InstanceExit? Exit)
{
    public const string Running = "running", Exited = "exited", Unverified = "unverified";
}

/// <summary>A verified registration. Replaced and ReplacedExit name the registration it continues, whose
/// KiCad process was proven to have ended, when the same instance ID moved to this new process.</summary>
public sealed record InstanceRegistration(InstanceRecord Instance, InstanceRecord? Replaced, InstanceExit? ReplacedExit);

public sealed partial class InstanceRegistry
{
    private sealed record ExitReplacement(int SchemaVersion, Guid OperationId, InstanceRecord Previous, InstanceExit Exit,
        InstanceRecord Replacement);

    /// <summary>The attached instances with what this server can prove about their processes, without
    /// contacting KiCad: "running" (this server observes the same process alive), "exited" (the exit is
    /// proven) or "unverified" (this server cannot observe the process).</summary>
    public IReadOnlyList<InstanceProcessStatus> Statuses() => connections.Values.OrderBy(c => c.Record.InstanceId, StringComparer.Ordinal)
        .Select(c =>
        {
            var exit = c.Process?.Probe();
            string state = exit is not null ? InstanceProcessStatus.Exited
                : c.Process?.VerifiedRunning == true ? InstanceProcessStatus.Running : InstanceProcessStatus.Unverified;
            return new InstanceProcessStatus(c.Record, state, exit);
        }).ToArray();

    /// <summary>The proven exit of the process that served <paramref name="epoch"/> of an instance, or
    /// null when this server cannot prove it ended. While that epoch is attached here, only the process
    /// this server observes for it decides: KiCad answered a handshake at that epoch, so neither a durable
    /// record nor a saved process ID overrides it, and an attached epoch whose process this server cannot
    /// observe is never proven to have ended. Otherwise the durable exit record decides, or a saved
    /// registration of that epoch whose recorded process (machine, boot, namespace, start time) is proven gone
    /// on this machine.</summary>
    public async Task<InstanceExit?> ProvenExitAsync(string instanceId, string epoch, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(instanceId, "D", out _) || string.IsNullOrWhiteSpace(epoch))
            throw new AutomationException("invalid_instance", "An instance UUID and a process epoch are required.");
        if (connections.TryGetValue(instanceId, out var connection) && connection.Record.Epoch == epoch)
            return connection.Process?.Probe();
        if (await ReadExitAsync(instanceId, epoch, token) is { } recorded) return recorded;
        if (File.Exists(Path.Combine(directory, instanceId + ".json")))
        {
            var saved = await ReadSavedAsync(instanceId, token);
            if (saved.Epoch == epoch && saved.ProcessId is { } pid && saved.ProcessStart is { } identity
                && ProcessIdentity.Ended(pid, identity))
            {
                var exit = new InstanceExit(instanceId, epoch, pid, null, null, InstanceExit.ProcessAbsentEvidence, DateTimeOffset.UtcNow);
                await WriteExitAsync(exit, token);
                return exit;
            }
        }
        return null;
    }

    // A file or folder name for one process epoch: the epoch itself when it is a UUID, as KiCad's are.
    private static string EpochName(string epoch) => Guid.TryParseExact(epoch, "D", out var parsed) ? parsed.ToString("D")
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(epoch)));

    private string ExitPath(string instanceId, string epoch) => Path.Combine(directory, "exits", instanceId, EpochName(epoch) + ".json");

    private async Task WriteExitAsync(InstanceExit exit, CancellationToken token)
    {
        string destination = ExitPath(exit.InstanceId, exit.Epoch);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) return; // The first proof of an exit is kept.
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(exit), token);
            try { File.Move(temporary, destination, false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<InstanceExit?> ReadExitAsync(string instanceId, string epoch, CancellationToken token)
    {
        string path = ExitPath(instanceId, epoch);
        try
        {
            var exit = JsonSerializer.Deserialize<InstanceExit>(await File.ReadAllTextAsync(path, token));
            if (exit is null || exit.InstanceId != instanceId || exit.Epoch != epoch || exit.ProcessId <= 0
                || exit.Evidence is not (InstanceExit.ExitStatusEvidence or InstanceExit.ProcessAbsentEvidence))
                throw new AutomationException("invalid_registry", "The recorded process exit is invalid.");
            return exit;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new AutomationException("invalid_registry", "The recorded process exit is invalid."); }
    }

    // A process this server did not start is observed by its process ID only when that ID runs this
    // instance's KiCad and is exactly the recorded process (same machine, boot, namespace and start time, read
    // right after the verified handshake or saved when that epoch was verified), so the exit of exactly
    // that process can be proven later. Anything else stays unverified.
    private InstanceProcessObserver? Observe(string instanceId, string epoch, int? processId, ProcessStartIdentity? identity)
    {
        if (processId is not { } pid || identity is null || !ProcessIdentity.Runs(pid, identity)
            || !ProcessIdentity.RunsInstance(pid, instanceId)) return null;
        return new ObservedProcessObserver(instanceId, epoch, pid, identity.StartTicks, RecordExit);
    }

    private Task RecordExit(InstanceExit exit) => WriteExitAsync(exit, CancellationToken.None);

    // Registration of a replacement for a registration whose process is proven to have ended: the same
    // instance ID continues with the new process epoch. The receipt keeps the previous registration and
    // how it ended, so nothing about the exited process is lost.
    private async Task<InstanceRecord> AdoptExitReplacementAsync(InstanceRecord previous, InstanceExit exit, NativeClient client,
        AutomationSession session, string endpoint, int? processId, ProcessStartIdentity? identity, InstanceProcessObserver? process,
        CancellationToken token)
    {
        if (session.InstanceId != previous.InstanceId || session.ProjectPath != previous.ProjectPath
            || session.Epoch == previous.Epoch || exit.Epoch != previous.Epoch || exit.InstanceId != previous.InstanceId)
            throw new AutomationException("instance_changed", "The replacement does not continue the exited instance and project.");
        var replacement = new InstanceRecord(previous.InstanceId, previous.ProjectPath, endpoint, session.Epoch, processId, DateTimeOffset.UtcNow,
            identity);
        using (await MetadataLease(token))
        {
            if (File.Exists(Path.Combine(directory, previous.InstanceId + ".json")))
            {
                var saved = await ReadSavedAsync(previous.InstanceId, token);
                if (saved.Epoch != previous.Epoch || saved.ProjectPath != previous.ProjectPath)
                    throw new AutomationException("instance_changed", "The saved registration changed while the replacement started; inspect it before continuing.");
            }
            string receipts = Directory.CreateDirectory(Path.Combine(directory, "exit-replacements")).FullName;
            string receipt = Path.Combine(receipts, Guid.NewGuid().ToString("D") + ".json");
            string pending = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new ExitReplacement(1,
                    Guid.Parse(Path.GetFileNameWithoutExtension(receipt)), previous, exit, replacement)), token);
                File.Move(pending, receipt);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            await WriteExitAsync(exit, token);
            await WriteRecordAsync(replacement, token);
        }
        connections[replacement.InstanceId] = new(replacement, client, session.Clone(), process);
        RetireLaunch(replacement.InstanceId);
        return replacement;
    }
}
