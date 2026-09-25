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
/// the status .NET reports; for a process ended by a signal it reports 128 plus the signal number,
/// which Signal gives back.</summary>
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

    /// <summary>One sentence part, for example "ended with exit status 137 (killed by signal 9, SIGKILL)".</summary>
    public string Describe() => Evidence == ProcessAbsentEvidence
        ? "is no longer running (its process no longer exists, so its exit status is unknown)"
        : Signal is { } signal
            ? $"ended with exit status {ExitCode} (killed by signal {signal}, {SignalName})"
            : $"ended with exit status {ExitCode}";

    internal static int? SignalOf(int exitCode) =>
        !OperatingSystem.IsWindows() && exitCode is > 128 and <= 128 + 64 ? exitCode - 128 : null;
}

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

/// <summary>A KiCad process this server did not start, identified by its process ID and, on
/// Linux, the kernel start counter read when it was attached, so a reused process ID is never
/// mistaken for the same process. Without that counter a present process ID proves nothing.</summary>
internal sealed class ObservedProcessObserver(string instanceId, string epoch, int processId, ulong? startTicks,
    Func<InstanceExit, Task>? recorder) : InstanceProcessObserver(instanceId, epoch, processId, recorder)
{
    public override InstanceExit? Probe()
    {
        if (KnownExit is { } known) return known;
        return ProcessIdentity.Gone(ProcessId, startTicks)
            ? Prove(new(InstanceId, Epoch, ProcessId, null, null, InstanceExit.ProcessAbsentEvidence, DateTimeOffset.UtcNow))
            : null;
    }

    public override bool VerifiedRunning => OperatingSystem.IsLinux() && startTicks is not null && Probe() is null;
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

    /// <summary>True only when the process is proven gone: it does not exist, it has ended, or on
    /// Linux its process ID now names a process started at another time or, when only the instance is
    /// known, a process that does not run that instance's KiCad.</summary>
    public static bool Gone(int processId, ulong? startTicks, string? instanceId = null)
    {
        if (processId <= 0) return false;
        if (OperatingSystem.IsLinux())
        {
            if (!Directory.Exists($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}")) return true;
            ulong? now = LinuxStartTicks(processId);
            // Present but unreadable proves nothing; ended, or started at another time, proves the exit.
            if (now is null) return IsEnded(processId);
            if (startTicks is { } then) return now != then;
            return instanceId is not null && !RunsInstance(processId, instanceId);
        }
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
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
    /// null when this server cannot prove it ended: from the attached process, the durable exit
    /// record, or a saved registration of that epoch whose process no longer exists.</summary>
    public async Task<InstanceExit?> ProvenExitAsync(string instanceId, string epoch, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(instanceId, "D", out _) || string.IsNullOrWhiteSpace(epoch))
            throw new AutomationException("invalid_instance", "An instance UUID and a process epoch are required.");
        // The process this server observes for that epoch decides; nothing weaker overrides it.
        if (connections.TryGetValue(instanceId, out var connection) && connection.Record.Epoch == epoch
            && connection.Process is { } observed)
            return observed.Probe();
        if (await ReadExitAsync(instanceId, epoch, token) is { } recorded) return recorded;
        if (File.Exists(Path.Combine(directory, instanceId + ".json")))
        {
            var saved = await ReadSavedAsync(instanceId, token);
            if (saved.Epoch == epoch && saved.ProcessId is { } pid && ProcessIdentity.Gone(pid, null, instanceId))
            {
                var exit = new InstanceExit(instanceId, epoch, pid, null, null, InstanceExit.ProcessAbsentEvidence, DateTimeOffset.UtcNow);
                await WriteExitAsync(exit, token);
                return exit;
            }
        }
        return null;
    }

    private async Task<InstanceExit?> ProvenExitAsync(InstanceRecord record, InstanceProcessObserver? process, CancellationToken token)
    {
        if (process is not null && process.Epoch == record.Epoch && process.Probe() is { } exit) return exit;
        return await ProvenExitAsync(record.InstanceId, record.Epoch, token);
    }

    private string ExitPath(string instanceId, string epoch)
    {
        string name = Guid.TryParseExact(epoch, "D", out var parsed) ? parsed.ToString("D")
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(epoch)));
        return Path.Combine(directory, "exits", instanceId, name + ".json");
    }

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
    // instance's KiCad, with the start counter read right after the verified handshake, so the exit of
    // exactly that process can be proven later. Anything else stays unverified.
    private InstanceProcessObserver? Observe(string instanceId, string epoch, int? processId)
    {
        if (processId is not { } pid || !ProcessIdentity.RunsInstance(pid, instanceId)) return null;
        return ProcessIdentity.LinuxStartTicks(pid) is { } ticks
            ? new ObservedProcessObserver(instanceId, epoch, pid, ticks, RecordExit) : null;
    }

    private Task RecordExit(InstanceExit exit) => WriteExitAsync(exit, CancellationToken.None);

    // Registration of a replacement for a registration whose process is proven to have ended: the same
    // instance ID continues with the new process epoch. The receipt keeps the previous registration and
    // how it ended, so nothing about the exited process is lost.
    private async Task<InstanceRecord> AdoptExitReplacementAsync(InstanceRecord previous, InstanceExit exit, NativeClient client,
        AutomationSession session, string endpoint, int? processId, InstanceProcessObserver? process, CancellationToken token)
    {
        if (session.InstanceId != previous.InstanceId || session.ProjectPath != previous.ProjectPath
            || session.Epoch == previous.Epoch || exit.Epoch != previous.Epoch || exit.InstanceId != previous.InstanceId)
            throw new AutomationException("instance_changed", "The replacement does not continue the exited instance and project.");
        var replacement = new InstanceRecord(previous.InstanceId, previous.ProjectPath, endpoint, session.Epoch, processId, DateTimeOffset.UtcNow);
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
