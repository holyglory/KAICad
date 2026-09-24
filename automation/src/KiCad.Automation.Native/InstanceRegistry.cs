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
    private sealed record Connection(InstanceRecord Record, NativeClient Client);
    // One immutable slot keeps the record and its epoch-pinned client together
    // when an explicitly verified replacement is adopted.
    private readonly ConcurrentDictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly HashSet<string> startingProjects = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly string directory = Path.GetFullPath(stateDirectory);

    public string StateDirectory => directory;

    public IReadOnlyList<InstanceRecord> List() => connections.Values.Select(value => value.Record).OrderBy(r => r.InstanceId).ToArray();

    public InstanceRecord Get(string instanceId) => Find(instanceId).Record;

    public NativeClient Client(string instanceId) => Find(instanceId).Client;

    private Connection Find(string instanceId) => connections.TryGetValue(instanceId, out var connection)
        ? connection : throw new AutomationException("unknown_instance", "The instance ID is not attached to this server.");

    public async Task<InstanceRecord> AttachAsync(string endpoint, string expectedInstanceId,
                                                 CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(expectedInstanceId, "D", out _))
            throw new AutomationException("invalid_instance", "An instance UUID is required.");
        NngTransport.ValidateEndpoint(endpoint);
        await changes.WaitAsync(cancellationToken);
        try { return await AttachCoreAsync(endpoint, expectedInstanceId, null, cancellationToken); }
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
                return attached;
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
            return attached;
        }
        finally { changes.Release(); }
    }

    public async Task<InstanceRecord> StartAsync(string executable, string projectPath,
                                                CancellationToken cancellationToken = default,
                                                bool? softwareRendering = null)
    {
        projectPath = Path.GetFullPath(projectPath);
        executable = Path.GetFullPath(executable);
        if (!File.Exists(projectPath) || Path.GetExtension(projectPath) != ".kicad_pro")
            throw new AutomationException("invalid_project", "An existing .kicad_pro file is required.");
        if (!File.Exists(executable))
            throw new AutomationException("missing_executable", "The native KiCad executable does not exist.");

        await changes.WaitAsync(cancellationToken);
        try
        {
            if (connections.Values.Any(r => r.Record.ProjectPath == projectPath) || !startingProjects.Add(projectPath))
                throw new AutomationException("project_owned", "This project already has an attached writer; use another worktree for an independent instance.");
        }
        finally { changes.Release(); }
        try
        {
            string id = Guid.NewGuid().ToString("D");
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
                        var ready = await probe.HandshakeAsync(deadline.Token);
                        await changes.WaitAsync(deadline.Token);
                        try
                        {
                            var attached = await AttachCoreAsync(endpoint, id, process.Id, deadline.Token, projectPath, ready.Epoch);
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
            finally { process.Dispose(); } // Dispose never kills the native process.
        }
        finally
        {
            await changes.WaitAsync(CancellationToken.None);
            try { startingProjects.Remove(projectPath); }
            finally { changes.Release(); }
        }
    }

    private async Task<InstanceRecord> AttachCoreAsync(string endpoint, string expectedId, int? processId,
                                                       CancellationToken cancellationToken,
                                                       string? expectedProject = null, string? expectedEpoch = null)
    {
        var client = new NativeClient(transport, endpoint, expectedEpoch);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.InstanceId != expectedId)
            throw new AutomationException("instance_mismatch", "The endpoint belongs to a different instance.");
        if ((expectedProject is not null && session.ProjectPath != expectedProject)
            || (expectedEpoch is not null && session.Epoch != expectedEpoch))
            throw new AutomationException("instance_changed", "The native session no longer matches the requested project or recorded epoch.");
        if (connections.TryGetValue(expectedId, out var existing)
            && (existing.Record.Epoch != session.Epoch || existing.Record.ProjectPath != session.ProjectPath || existing.Record.Endpoint != endpoint))
            throw new AutomationException("instance_changed", "An attached identity cannot be rebound to another process, project or endpoint.");
        if (connections.Values.Any(r => r.Record.ProjectPath == session.ProjectPath && r.Record.InstanceId != expectedId))
            throw new AutomationException("project_owned", "A different instance already owns this project's registry entry.");
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
        var connection = new Connection(record, existing?.Client ?? client);
        await WriteRecordAsync(record, cancellationToken);
        connections[record.InstanceId] = connection;
        return record;
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
