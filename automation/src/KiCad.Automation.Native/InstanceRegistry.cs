using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record InstanceRecord(string InstanceId, string ProjectPath, string Endpoint,
                                    string Epoch, int? ProcessId, DateTimeOffset VerifiedAt);

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
            var attached = await AttachCoreAsync(saved.Endpoint, saved.InstanceId, saved.ProcessId,
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
    /// (this server saw its exit status, or its process no longer exists), the new process continues
    /// that instance ID with a new epoch, so recovery records and journals that name the instance can
    /// be reattached; the result names the replaced registration and how its process ended. A project
    /// whose registered KiCad may still run is refused as before.</summary>
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
                replacedExit = await ProvenExitAsync(owner.Record, owner.Process, cancellationToken)
                    ?? throw new AutomationException("project_owned", "This project already has an attached writer; use another worktree for an independent instance.");
                replacing = owner.Record;
            }
            else if (await LatestSavedAsync(projectPath, cancellationToken) is { } saved
                     && await ProvenExitAsync(saved.InstanceId, saved.Epoch, cancellationToken) is { } exit)
            {
                replacing = saved; replacedExit = exit;
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
                            var watcher = new ChildProcessObserver(id, ready.Epoch, process, RecordExit);
                            observed = true;
                            var attached = await AttachCoreAsync(endpoint, id, process.Id, deadline.Token, projectPath, ready.Epoch,
                                replaceExited: replacing is not null, child: watcher);
                            RetireLaunch(id);
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
        // A process this server started is known by its child ID.
        if (child is null && session.ProcessId is > 0 and <= int.MaxValue && ProcessIdentity.RunsInstance((int)session.ProcessId, expectedId))
            processId = (int)session.ProcessId;
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
            var exit = await ProvenExitAsync(previous, existing?.Process, cancellationToken)
                ?? throw new AutomationException("instance_changed", $"Instance {expectedId} is registered to another KiCad process "
                    + $"(epoch {previous.Epoch}) that this server cannot prove has ended. Inspect that process; a replacement is adopted only after its exit.");
            var observer = child ?? Observe(expectedId, session.Epoch, processId);
            client.Process = observer;
            return new(await AdoptExitReplacementAsync(previous, exit, client, session, endpoint, processId, observer, cancellationToken),
                previous, exit);
        }
        var record = new InstanceRecord(session.InstanceId, session.ProjectPath, endpoint,
                                        session.Epoch, processId, DateTimeOffset.UtcNow);
        using var lease = await MetadataLease(cancellationToken);
        if (File.Exists(Path.Combine(directory, record.InstanceId + ".json")))
        {
            var saved = await ReadSavedAsync(record.InstanceId, cancellationToken);
            if (saved.Epoch != record.Epoch || saved.ProjectPath != record.ProjectPath || saved.Endpoint != record.Endpoint)
                throw new AutomationException("instance_changed", "A saved identity requires explicit verified replacement, not ordinary attachment.");
            if (record.ProcessId is null) record = record with { ProcessId = saved.ProcessId };
        }
        // A repeated attachment keeps the shared serialized client and what it knows about the process,
        // but records this handshake.
        var process = existing?.Process ?? child ?? Observe(record.InstanceId, record.Epoch, record.ProcessId);
        if (existing is null) client.Process = process;
        var connection = new Connection(record, existing?.Client ?? client, session.Clone(), process);
        await WriteRecordAsync(record, cancellationToken);
        connections[record.InstanceId] = connection;
        return new(record, null, null);
    }

    // The most recently verified saved registration of a project that no attached instance holds.
    // Unreadable records are skipped here; reattaching them reports why.
    private async Task<InstanceRecord?> LatestSavedAsync(string projectPath, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return null;
        InstanceRecord? latest = null;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            InstanceRecord saved;
            try { saved = await ReadSavedAsync(Path.GetFileNameWithoutExtension(file), token); }
            catch (AutomationException) { continue; }
            if (saved.ProjectPath == projectPath && (latest is null || saved.VerifiedAt > latest.VerifiedAt)) latest = saved;
        }
        return latest;
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
