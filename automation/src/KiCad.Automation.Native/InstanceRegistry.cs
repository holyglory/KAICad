using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>A verified registration. ProcessId and ProcessStart name the KiCad process of that epoch only
/// when this server verified it runs the instance (or started it); ProcessStart is null off Linux and in
/// records written before it existed, and then a saved registration never proves that process ended.</summary>
public sealed record InstanceRecord(string InstanceId, string ProjectPath, string Endpoint,
                                    string Epoch, int? ProcessId, DateTimeOffset VerifiedAt,
                                    ProcessStartIdentity? ProcessStart = null);

/// <summary>The registry is connection metadata, not a second source of design truth.</summary>
public sealed partial class InstanceRegistry(INativeTransport transport, string stateDirectory,
    Action<ProcessStartInfo>? configureProcess = null)
{
    // Process is what this server can prove about the KiCad process behind the connection: the exit
    // status of a process it started, or the process ID and start time of one it attached to.
    private sealed record Connection(InstanceRecord Record, NativeClient Client, AutomationSession Handshake,
        InstanceProcessObserver? Process = null);
    // One immutable slot keeps the record, its epoch-pinned client and the handshake that
    // verified it together, so an explicitly verified replacement or a reattachment replaces
    // all three at once.
    private readonly ConcurrentDictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly HashSet<string> startingProjects = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly string directory = Path.GetFullPath(stateDirectory);

    public string StateDirectory => directory;

    public IReadOnlyList<InstanceRecord> List() => connections.Values.Select(value => value.Record).OrderBy(r => r.InstanceId).ToArray();

    public InstanceRecord Get(string instanceId) => Find(instanceId).Record;

    /// <summary>The epoch-pinned client of an attached instance. An instance whose process is proven to
    /// have ended fails here with instance_exited, which says how it ended and how to continue.</summary>
    public NativeClient Client(string instanceId)
    {
        var connection = Find(instanceId);
        if (connection.Process?.KnownExit is { } exit) throw connection.Process.ExitedError(exit, requestMayHaveReached: false);
        return connection.Client;
    }

    /// <summary>The handshake this server recorded when it last verified the instance: at attach,
    /// start, reattach or an adopted replacement, each of which replaces the previous one. Reading it
    /// never contacts KiCad, so offline planning can classify exactly as apply does (CN-1 I10;
    /// decision n39ac0ccc5c9270f2). Null when the instance is not attached to this server; a saved
    /// registration from an earlier server is not a handshake until it is reattached. Every read is
    /// a copy.</summary>
    public AutomationSession? AttachedHandshake(string instanceId) =>
        connections.TryGetValue(instanceId, out var connection) ? connection.Handshake.Clone() : null;

    private Connection Find(string instanceId) => connections.TryGetValue(instanceId, out var connection)
        ? connection : throw new AutomationException("unknown_instance", "The instance ID is not attached to this server.");

    public async Task<InstanceRecord> AttachAsync(string endpoint, string expectedInstanceId,
                                                 CancellationToken cancellationToken = default) =>
        (await AttachInstanceAsync(endpoint, expectedInstanceId, cancellationToken)).Instance;

    /// <summary>Attach an explicitly identified instance. A different KiCad process under an instance ID
    /// this registry knows continues that ID only when the registered process is proven to have ended;
    /// the result then names the registration it replaced.</summary>
    public async Task<InstanceRegistration> AttachInstanceAsync(string endpoint, string expectedInstanceId,
                                                             CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(expectedInstanceId, "D", out _))
            throw new AutomationException("invalid_instance", "An instance UUID is required.");
        NngTransport.ValidateEndpoint(endpoint);
        await changes.WaitAsync(cancellationToken);
        // A registration whose KiCad process is proven to have ended continues with the attached process.
        try { return await AttachCoreAsync(endpoint, expectedInstanceId, null, cancellationToken, replaceExited: true); }
        finally { changes.Release(); }
    }

    public async Task<InstanceRecord> ReattachAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(instanceId, "D", out _))
            throw new AutomationException("invalid_instance", "An instance UUID is required.");
        string path = Path.Combine(directory, instanceId + ".json");
        if (!File.Exists(path))
        {
            var launch = await ReadLaunchAsync(instanceId, cancellationToken);
            await changes.WaitAsync(cancellationToken);
            try
            {
                // The launch receipt's process ID is the process a previous server started, which may be a
                // launcher rather than KiCad: it stays a diagnostic hint, never an identity that proves an exit.
                var attached = await AttachCoreAsync(launch.Endpoint, instanceId, launch.ProcessId,
                    cancellationToken, launch.ProjectPath);
                RetireLaunch(instanceId);
                return attached.Instance;
            }
            finally { changes.Release(); }
        }
        InstanceRecord saved = await ReadSavedAsync(instanceId, cancellationToken);
        await changes.WaitAsync(cancellationToken);
        try
        {
            // The same epoch keeps the process recorded when it was verified (AttachCoreAsync copies it).
            var attached = await AttachCoreAsync(saved.Endpoint, saved.InstanceId, null,
                cancellationToken, saved.ProjectPath, saved.Epoch);
            RetireLaunch(instanceId);
            return attached.Instance;
        }
        finally { changes.Release(); }
    }

    public async Task<InstanceRecord> StartAsync(string executable, string projectPath,
                                                CancellationToken cancellationToken = default,
                                                bool? softwareRendering = null) =>
        (await StartInstanceAsync(executable, projectPath, cancellationToken, softwareRendering)).Instance;

    /// <summary>Start KiCad for a project. When the project's registered KiCad is proven to have ended
    /// (this server saw its exit status, or its recorded process no longer exists), the new process
    /// continues that instance ID with a new epoch, so recovery records and journals that name the
    /// instance can be reattached; the result names the replaced registration and how its process ended.
    /// A project whose attached KiCad may still run, or whose saved registration's KiCad is shown to be
    /// still running this instance, is refused (project_owned). A saved registration whose KiCad this
    /// server can neither prove ended nor show running does not block the start: the new KiCad gets a new
    /// instance ID, as before, and KiCad itself refuses a project another KiCad has open.</summary>
    public async Task<InstanceRegistration> StartInstanceAsync(string executable, string projectPath,
                                                             CancellationToken cancellationToken = default,
                                                             bool? softwareRendering = null)
    {
        projectPath = Path.GetFullPath(projectPath);
        executable = Path.GetFullPath(executable);
        if (!File.Exists(projectPath) || Path.GetExtension(projectPath) != ".kicad_pro")
            throw new AutomationException("invalid_project", "An existing .kicad_pro file is required.");
        if (!File.Exists(executable))
            throw new AutomationException("missing_executable", "The native KiCad executable does not exist.");

        InstanceRecord? replacing = null;
        InstanceExit? replacedExit = null;
        await changes.WaitAsync(cancellationToken);
        try
        {
            if (startingProjects.Contains(projectPath))
                throw new AutomationException("project_owned", "This project already has an attached writer; use another worktree for an independent instance.");
            if (connections.Values.FirstOrDefault(r => r.Record.ProjectPath == projectPath) is { } owner)
            {
                replacedExit = await ProvenExitAsync(owner.Record.InstanceId, owner.Record.Epoch, cancellationToken)
                    ?? throw new AutomationException("project_owned", "This project already has an attached writer; use another worktree for an independent instance.");
                replacing = owner.Record;
            }
            else
            {
                var saved = await SavedForProjectAsync(projectPath, cancellationToken);
                if (saved.FirstOrDefault(r => r.ProcessId is { } pid && r.ProcessStart is { } identity
                        && ProcessIdentity.Runs(pid, identity) && ProcessIdentity.RunsInstance(pid, r.InstanceId)) is { } running)
                    throw new AutomationException("project_owned", $"KiCad instance {running.InstanceId} (process {running.ProcessId}) from a saved "
                        + "registration still runs this project; reattach it with kicad_instance_reattach instead of starting another KiCad.");
                if (saved.Count != 0 && await ProvenExitAsync(saved[0].InstanceId, saved[0].Epoch, cancellationToken) is { } exit)
                {
                    replacing = saved[0]; replacedExit = exit;
                }
            }
            startingProjects.Add(projectPath);
        }
        finally { changes.Release(); }
        try
        {
            string id = replacing?.InstanceId ?? Guid.NewGuid().ToString("D");
            string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
            string socket = Path.Combine(runtime, "api.sock");
            string endpoint = NativeIpcEndpoint.FromSocketPath(socket);
            Directory.CreateDirectory(runtime);
            // KiCad started again under the same instance ID writes into the same runtime folder.
            KeepEarlierProcessLogs(runtime);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "--new", "--automation", id, "--api-socket", socket,
                                                "--automation-log", Path.Combine(runtime, "native.log") })
                start.ArgumentList.Add(argument);
            if (softwareRendering ?? OperatingSystem.IsLinux()) start.ArgumentList.Add("--software-rendering");
            start.ArgumentList.Add(projectPath);
            configureProcess?.Invoke(start);
            var launch = new UnverifiedInstanceLaunch(id, projectPath, endpoint, null, DateTimeOffset.UtcNow);
            await SaveLaunchAsync(launch, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Process process = Process.Start(start) ?? throw new AutomationException("start_failed", "KiCad could not be started.");
            // Once attached, the process belongs to its exit observer, which disposes it when KiCad ends.
            bool observed = false;
            // The native --automation-log option redirects its own descriptors, so
            // editor lifetime does not depend on MCP's diagnostic pipes (SA-04).
            _ = CaptureAsync(process.StandardOutput, Path.Combine(runtime, "bootstrap.stdout.log"));
            _ = CaptureAsync(process.StandardError, Path.Combine(runtime, "bootstrap.stderr.log"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                // The launch intent is already durable if MCP disconnects
                // between process creation and this diagnostic PID update.
                await SaveLaunchAsync(launch with { ProcessId = process.Id }, CancellationToken.None);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        throw new AutomationException("start_failed", StartFailure(process.ExitCode, runtime));
                    try
                    {
                        // Readiness probes do not hold the registry gate: separate
                        // projects must be able to start concurrently.
                        var probe = new NativeClient(transport, endpoint);
                        AutomationSession ready;
                        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                        {
                            // KiCad that ends while a probe waits for its reply (for example after
                            // refusing a project another KiCad holds) never answers it. Stop waiting
                            // when KiCad ends, so the start reports its exit at once instead of after
                            // the probe's reply timeout.
                            var handshake = probe.HandshakeAsync(attempt.Token);
                            var exited = process.WaitForExitAsync(attempt.Token);
                            bool ended = await Task.WhenAny(handshake, exited) != handshake;
                            await attempt.CancelAsync();
                            if (ended)
                            {
                                // The next pass reports KiCad's exit, or the start deadline.
                                try { await handshake; }
                                catch (Exception) { }
                                continue;
                            }
                            ready = await handshake;
                        }
                        await changes.WaitAsync(deadline.Token);
                        try
                        {
                            // KiCad names its own process in the handshake (0 from a KiCad built before that
                            // field). The process this server started witnesses KiCad's exit only when it is
                            // that KiCad: a launcher that started KiCad and ended is not, so its exit is never
                            // reported as KiCad's. The process KiCad names is then observed instead, when it
                            // runs this instance, or the instance stays unverified. The same epoch is attached
                            // below, so its handshake names the same process.
                            bool startedKiCad = ready.ProcessId == 0 || ready.ProcessId == (uint)process.Id;
                            var watcher = startedKiCad ? new ChildProcessObserver(id, ready.Epoch, process, RecordExit) : null;
                            observed = watcher is not null;
                            var attached = await AttachCoreAsync(endpoint, id, startedKiCad ? process.Id : null, deadline.Token,
                                projectPath, ready.Epoch, replaceExited: replacing is not null, child: watcher);
                            RetireLaunch(id);
                            RecordLogEpoch(runtime, attached.Instance.Epoch);
                            return attached;
                        }
                        finally { changes.Release(); }
                    }
                    catch (NngException) { }
                    catch (NativeApiException e) when (e.Status is 4 or 7) { }
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AutomationException("startup_timeout", $"KiCad did not become ready; it was not killed. Inspect {runtime} and reattach instance {id} at {endpoint} if it recovers.");
            }
            finally { if (!observed) process.Dispose(); } // Dispose never kills the native process.
        }
        finally
        {
            await changes.WaitAsync(CancellationToken.None);
            try { startingProjects.Remove(projectPath); }
            finally { changes.Release(); }
        }
    }

    // processId is the KiCad process this server started when child observes it, and otherwise an unverified
    // hint (a launch receipt's process). A process identity is recorded only for a verified KiCad process: that
    // child, or the process the handshake names when it runs this instance; never for whatever holds an
    // unverified process ID now.
    private async Task<InstanceRegistration> AttachCoreAsync(string endpoint, string expectedId, int? processId,
                                                       CancellationToken cancellationToken,
                                                       string? expectedProject = null, string? expectedEpoch = null,
                                                       bool replaceExited = false, InstanceProcessObserver? child = null)
    {
        var client = new NativeClient(transport, endpoint, expectedEpoch);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != expectedId)
            throw new AutomationException("instance_mismatch", "The endpoint belongs to a different instance.");
        if ((expectedProject is not null && session.ProjectPath != expectedProject)
            || (expectedEpoch is not null && session.Epoch != expectedEpoch))
            throw new AutomationException("instance_changed", "The native session no longer matches the requested project or recorded epoch.");
        // KiCad reports its own process ID in the handshake; it is recorded once it is shown to run this instance.
        // A process this server started, and that KiCad names as itself, is known by its child ID.
        bool verified = child is not null;
        if (child is null && session.ProcessId is > 0 and <= int.MaxValue && ProcessIdentity.RunsInstance((int)session.ProcessId, expectedId))
        {
            processId = (int)session.ProcessId;
            verified = true;
        }
        // Which process that is, recorded right after the verified handshake so a later server can prove it ended.
        var identity = verified ? ProcessIdentity.Record(processId) : null;
        // A different process under a known identity is adopted only as the replacement of a
        // registration whose process is proven to have ended; anything else stays a changed identity.
        InstanceRecord? previous = null;
        if (connections.TryGetValue(expectedId, out var existing)
            && (existing.Record.Epoch != session.Epoch || existing.Record.ProjectPath != session.ProjectPath || existing.Record.Endpoint != endpoint))
        {
            if (!replaceExited || existing.Record.Epoch == session.Epoch || existing.Record.ProjectPath != session.ProjectPath)
                throw new AutomationException("instance_changed", "An attached identity cannot be rebound to another process, project or endpoint.");
            previous = existing.Record;
        }
        if (connections.Values.Any(r => r.Record.ProjectPath == session.ProjectPath && r.Record.InstanceId != expectedId))
            throw new AutomationException("project_owned", "A different instance already owns this project's registry entry.");
        if (previous is null && existing is null && File.Exists(Path.Combine(directory, expectedId + ".json")))
        {
            var saved = await ReadSavedAsync(expectedId, cancellationToken);
            if (saved.Epoch != session.Epoch && replaceExited && saved.ProjectPath == session.ProjectPath) previous = saved;
        }
        if (previous is not null)
        {
            var exit = await ProvenExitAsync(previous.InstanceId, previous.Epoch, cancellationToken)
                ?? throw new AutomationException("instance_changed", $"Instance {expectedId} is registered to another KiCad process "
                    + $"(epoch {previous.Epoch}) that this server cannot prove has ended. Inspect that process; a replacement is adopted only after its exit.");
            var observer = child ?? Observe(expectedId, session.Epoch, processId, identity);
            client.Process = observer;
            return new(await AdoptExitReplacementAsync(previous, exit, client, session, endpoint, processId, identity, observer, cancellationToken),
                previous, exit);
        }
        var record = new InstanceRecord(session.InstanceId, session.ProjectPath, endpoint,
                                        session.Epoch, processId, DateTimeOffset.UtcNow, identity);
        using var lease = await MetadataLease(cancellationToken);
        if (File.Exists(Path.Combine(directory, record.InstanceId + ".json")))
        {
            var saved = await ReadSavedAsync(record.InstanceId, cancellationToken);
            if (saved.Epoch != record.Epoch || saved.ProjectPath != record.ProjectPath || saved.Endpoint != record.Endpoint)
                throw new AutomationException("instance_changed", "A saved identity requires explicit verified replacement, not ordinary attachment.");
            // The same epoch is the same process: keep what was recorded when it was first verified. It is
            // observed only when it is still exactly that process in this server's namespace.
            if (record.ProcessId is null) record = record with { ProcessId = saved.ProcessId, ProcessStart = saved.ProcessStart };
        }
        // A repeated attachment keeps the shared serialized client and what it knows about the process,
        // but records this handshake.
        var process = existing?.Process ?? child ?? Observe(record.InstanceId, record.Epoch, record.ProcessId, record.ProcessStart);
        if (existing is null) client.Process = process;
        var connection = new Connection(record, existing?.Client ?? client, session.Clone(), process);
        await WriteRecordAsync(record, cancellationToken);
        connections[record.InstanceId] = connection;
        return new(record, null, null);
    }

    // The saved registrations of a project that no attached instance holds, most recently verified first.
    // Unreadable records are skipped here; reattaching them reports why.
    private async Task<IReadOnlyList<InstanceRecord>> SavedForProjectAsync(string projectPath, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];
        var found = new List<InstanceRecord>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            InstanceRecord saved;
            try { saved = await ReadSavedAsync(Path.GetFileNameWithoutExtension(file), token); }
            catch (AutomationException) { continue; }
            if (saved.ProjectPath == projectPath) found.Add(saved);
        }
        return found.OrderByDescending(saved => saved.VerifiedAt).ToArray();
    }

    // The logs a started KiCad writes into its runtime folder, and the file naming the epoch that wrote them.
    private static readonly string[] ProcessLogs = ["native.log", "bootstrap.stdout.log", "bootstrap.stderr.log"];
    private const string LogEpochFile = "logs.epoch";

    // Before KiCad starts again in a runtime folder, the logs of the process that ran there before move to
    // epochs/<its epoch>/, the epoch this server verified for it; logs of a start that never answered a
    // handshake move to epochs/unverified-<time>/. So every process keeps its own logs and none is appended
    // to or overwritten by the next one. A failed move leaves the files in place (the new process's logs
    // then follow them) rather than failing the start.
    private static void KeepEarlierProcessLogs(string runtime)
    {
        string marker = Path.Combine(runtime, LogEpochFile);
        string[] present = ProcessLogs.Where(name => File.Exists(Path.Combine(runtime, name))).ToArray();
        try
        {
            if (present.Length == 0) { File.Delete(marker); return; }
            string? epoch = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            string folder = epoch is { Length: > 0 } ? EpochName(epoch)
                : "unverified-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);
            string destination = Path.Combine(runtime, "epochs", folder);
            if (Directory.Exists(destination)) destination += "-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(destination);
            foreach (string name in present) File.Move(Path.Combine(runtime, name), Path.Combine(destination, name));
            File.Delete(marker);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void RecordLogEpoch(string runtime, string epoch)
    {
        try { File.WriteAllText(Path.Combine(runtime, LogEpochFile), epoch); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // KiCad logs why a start failed ("Error: ..." lines in its automation log, for example a
    // project another KiCad holds), so the answer repeats those reasons instead of only pointing
    // at the files. The log is written by the exited process and read here once, bounded.
    private static string StartFailure(int exitCode, string runtime)
    {
        var reasons = new List<string>();
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(runtime, "native.log")))
            {
                int error = line.IndexOf(": Error: ", StringComparison.Ordinal);
                if (error < 0) continue;
                string reason = line[(error + ": Error: ".Length)..].Trim();
                if (reason.Length == 0) continue;
                reasons.Add(reason.Length > 400 ? reason[..400] + "..." : reason);
                if (reasons.Count > 5) reasons.RemoveAt(0);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (reasons.Count == 0) return $"KiCad exited with code {exitCode}; inspect {runtime}.";
        string logged = string.Join(" ", reasons.Select(reason => reason.EndsWith('.') ? reason : reason + "."));
        return $"KiCad exited with code {exitCode}: {logged} Inspect {runtime}.";
    }

    private static async Task CaptureAsync(StreamReader source, string path)
    {
        try
        {
            await using var output = new StreamWriter(path, false);
            var buffer = new char[4096];
            int read;
            while ((read = await source.ReadAsync(buffer)) != 0)
                await output.WriteAsync(buffer.AsMemory(0, read));
        }
        catch (IOException) { /* Native process diagnostics remain in native.log. */ }
        catch (ObjectDisposedException) { }
    }
}
