using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class InstanceRegistryTests
{
    [TestMethod]
    public async Task PlatformLocalLaunchReceiptUsesTheSameEndpointAsNativeStartup()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-local-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "project.kicad_pro") };
            string endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(transport.InstanceId), "api.sock"));
            var launch = new UnverifiedInstanceLaunch(transport.InstanceId, transport.ProjectPath, endpoint, 12345, DateTimeOffset.UtcNow);
            string receipt = await WriteLaunch(state, launch);
            var registry = new InstanceRegistry(transport, state);
            var attached = await registry.ReattachAsync(transport.InstanceId);
            Assert.AreEqual(endpoint, attached.Endpoint); Assert.AreEqual(launch.ProcessId, attached.ProcessId);
            Assert.IsFalse(File.Exists(receipt));
            Assert.AreEqual(transport.Epoch, registry.Client(transport.InstanceId).Epoch);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task InterruptedLaunchIsUnverifiedUntilIdentityAndProjectAreConfirmed()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            var launch = new UnverifiedInstanceLaunch(transport.InstanceId, transport.ProjectPath,
                LaunchEndpoint(transport.InstanceId), 12345, DateTimeOffset.UtcNow);
            string receipt = await WriteLaunch(state, launch);
            var registry = new InstanceRegistry(transport, state);
            Assert.AreEqual(0, registry.List().Count);
            CollectionAssert.AreEqual(new[] { launch }, (await registry.PendingLaunchesAsync()).ToArray());
            var attached = await registry.ReattachAsync(launch.InstanceId);
            Assert.AreEqual(launch.ProcessId, attached.ProcessId);
            Assert.AreEqual(transport.Epoch, attached.Epoch);
            Assert.AreEqual(launch.ProjectPath, attached.ProjectPath);
            Assert.AreEqual(1, registry.List().Count);
            Assert.IsFalse(File.Exists(receipt));
            Assert.AreEqual(0, (await registry.PendingLaunchesAsync()).Count);
            var restarted = new InstanceRegistry(transport, state);
            Assert.AreEqual(attached.ProcessId, (await restarted.ReattachAsync(launch.InstanceId)).ProcessId);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    [DataRow(false, "instance_changed")]
    [DataRow(true, "instance_mismatch")]
    public async Task LaunchMismatchPreservesReceiptWithoutRegistering(bool wrongIdentity, string code)
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            string id = wrongIdentity ? Guid.NewGuid().ToString("D") : transport.InstanceId;
            var launch = new UnverifiedInstanceLaunch(id, Path.Combine(state, "other.kicad_pro"),
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow);
            string receipt = await WriteLaunch(state, launch);
            var registry = new InstanceRegistry(transport, state);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual(code, error.Code);
            Assert.AreEqual(0, registry.List().Count);
            Assert.IsTrue(File.Exists(receipt));
            Assert.IsFalse(File.Exists(Path.Combine(state, id + ".json")));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task LaunchReceiptCannotBypassPreviouslyVerifiedEpoch()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            string id = transport.InstanceId;
            var launch = new UnverifiedInstanceLaunch(id, transport.ProjectPath,
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow);
            await new InstanceRegistry(transport, state).AttachAsync(launch.Endpoint, id);
            string receipt = await WriteLaunch(state, launch);
            transport.Epoch = "replacement-process";
            var registry = new InstanceRegistry(transport, state);
            Assert.AreEqual(0, (await registry.PendingLaunchesAsync()).Count);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("instance_changed", error.Code);
            Assert.IsTrue(File.Exists(receipt));
            Assert.AreEqual(0, registry.List().Count);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task MalformedReceiptDoesNotBecomeAConnection()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport();
            string id = transport.InstanceId;
            var registry = new InstanceRegistry(transport, state);
            var missing = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("unknown_instance", missing.Code);
            string receipt = await WriteLaunch(state, new(id, "relative.kicad_pro",
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow));
            var invalid = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("invalid_registry", invalid.Code);
            await File.WriteAllTextAsync(receipt, "{invalid");
            invalid = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("invalid_registry", invalid.Code);
            Assert.AreEqual(0, registry.List().Count);
        }
        finally { Directory.Delete(state, true); }
    }

    private static async Task<string> WriteLaunch(string state, UnverifiedInstanceLaunch launch)
    {
        string directory = Directory.CreateDirectory(Path.Combine(state, "launches")).FullName;
        string path = Path.Combine(directory, launch.InstanceId + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(launch));
        return path;
    }

    [TestMethod]
    public async Task CorruptSavedSessionDoesNotFallBackToLaunchReceipt()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            string id = transport.InstanceId;
            await WriteLaunch(state, new(id, transport.ProjectPath,
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow));
            await File.WriteAllTextAsync(Path.Combine(state, id + ".json"), "{}");
            var registry = new InstanceRegistry(transport, state);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("invalid_registry", error.Code);
            Assert.IsNull(transport.LastRequest, "Invalid records must fail before native communication.");
            Assert.AreEqual(0, registry.List().Count);
            error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.SavedSessionsAsync());
            Assert.AreEqual("invalid_registry", error.Code);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task ReattachVerifiesSavedEpochAndSharesSerializedClient()
    {
        string state = Directory.CreateTempSubdirectory("kicad-registry-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            var first = new InstanceRegistry(transport, state);
            await first.AttachAsync(LaunchEndpoint(transport.InstanceId), transport.InstanceId);
            Assert.AreSame(first.Client(transport.InstanceId), first.Client(transport.InstanceId));
            var second = new InstanceRegistry(transport, state);
            Assert.AreEqual(0, second.List().Count);
            Assert.AreEqual(1, (await second.SavedSessionsAsync()).Count);
            await second.ReattachAsync(transport.InstanceId);
            Assert.AreEqual(1, second.List().Count);
            transport.Epoch = "restarted";
            Assert.AreEqual("fixture-epoch", (await second.SavedSessionsAsync())[0].Epoch,
                "Saved discovery must remain historical and must not implicitly contact or adopt a new peer.");
            AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                new InstanceRegistry(transport, state).ReattachAsync(transport.InstanceId));
            Assert.AreEqual("instance_changed", error.Code);
            Assert.IsTrue(File.Exists(Path.Combine(state, transport.InstanceId + ".json")));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task WrongIdentityDoesNotCreateARegistration()
    {
        string state = Directory.CreateTempSubdirectory("kicad-registry-test-").FullName;
        try
        {
            var registry = new InstanceRegistry(new NativeClientTests.FixtureTransport(), state);
            AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                registry.AttachAsync("ipc:///tmp/registry-fixture.sock", Guid.NewGuid().ToString("D")));
            Assert.AreEqual("instance_mismatch", error.Code);
            Assert.AreEqual(0, registry.List().Count);
            Assert.AreEqual(0, Directory.GetFiles(state).Length);
        }
        finally { Directory.Delete(state, true); }
    }

    private static string LaunchEndpoint(string id) => NativeIpcEndpoint.FromSocketPath(
        Path.Combine(NativeIpcEndpoint.RuntimeDirectory(id), "api.sock"));

    // Isolated registry rules behind NativeSessionTests.NativeCrashKeepsXmlAndRegistryTruthful, which drives the same
    // paths against real KiCad processes. Here stand-in processes make each lifetime deterministic: a process named by
    // the handshake is observed only when it runs this instance, and another process under the same identity is adopted
    // only once the registered one is proven to have ended, also after a server restart.
    [TestMethod]
    public async Task AnAttachedInstanceIsReplacedOnlyAfterItsObservedProcessEnds()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Process observation reads Linux /proc."); return; }
        string state = Directory.CreateTempSubdirectory("kicad-exit-attach-").FullName;
        var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
        string id = transport.InstanceId, endpoint = LaunchEndpoint(id);
        using var first = StandIn(id);
        Process? second = null;
        try
        {
            // A handshake naming a process that does not run this instance proves nothing.
            using (var unrelated = StandIn(Guid.NewGuid().ToString("D")))
            {
                transport.ProcessId = (uint)unrelated.Id;
                var other = new InstanceRegistry(transport, Directory.CreateTempSubdirectory("kicad-exit-unrelated-").FullName);
                await other.AttachAsync(endpoint, id);
                Assert.AreEqual(InstanceProcessStatus.Unverified, other.Statuses().Single().State);
                Assert.IsNull(other.Get(id).ProcessId, "Only a process that runs this instance is recorded.");
                unrelated.Kill(entireProcessTree: true);
                await unrelated.WaitForExitAsync();
                Directory.Delete(other.StateDirectory, true);
            }

            transport.ProcessId = (uint)first.Id;
            var registry = new InstanceRegistry(transport, state);
            await registry.AttachAsync(endpoint, id);
            var running = registry.Statuses().Single();
            Assert.AreEqual(InstanceProcessStatus.Running, running.State);
            Assert.AreEqual(first.Id, running.Instance.ProcessId);

            // While that process runs, another process under the same identity is a changed instance.
            transport.Epoch = "replacement-epoch";
            var refused = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.AttachAsync(endpoint, id));
            Assert.AreEqual("instance_changed", refused.Code);
            StringAssert.Contains(refused.Message, "cannot prove has ended");
            Assert.AreEqual("fixture-epoch", registry.Get(id).Epoch);
            Assert.IsNull(await registry.ProvenExitAsync(id, "fixture-epoch"));

            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync();
            var ended = registry.Statuses().Single();
            Assert.AreEqual(InstanceProcessStatus.Exited, ended.State);
            Assert.AreEqual(InstanceExit.ProcessAbsentEvidence, ended.Exit!.Evidence);
            Assert.IsNull(ended.Exit.ExitCode, "Only the parent of a process learns its exit status.");
            Assert.AreEqual("instance_exited", Assert.ThrowsExactly<AutomationException>(() => registry.Client(id)).Code);

            second = StandIn(id);
            transport.ProcessId = (uint)second.Id;
            var adopted = await registry.AttachInstanceAsync(endpoint, id);
            Assert.AreEqual("replacement-epoch", adopted.Instance.Epoch);
            Assert.AreEqual(second.Id, adopted.Instance.ProcessId);
            Assert.AreEqual("fixture-epoch", adopted.Replaced!.Epoch);
            Assert.AreEqual(first.Id, adopted.ReplacedExit!.ProcessId);
            Assert.AreEqual("replacement-epoch", registry.Client(id).Epoch);
            Assert.AreEqual(InstanceProcessStatus.Running, registry.Statuses().Single().State);
            Assert.HasCount(1, Directory.GetFiles(Path.Combine(state, "exit-replacements"), "*.json"));

            // A restarted server still proves how the replaced process ended, and reattaches the replacement.
            var restarted = new InstanceRegistry(transport, state);
            Assert.AreEqual(first.Id, (await restarted.ProvenExitAsync(id, "fixture-epoch"))!.ProcessId);
            Assert.AreEqual("replacement-epoch", (await restarted.ReattachAsync(id)).Epoch);
            Assert.IsNull(await restarted.ProvenExitAsync(id, "replacement-epoch"));
        }
        finally
        {
            foreach (var process in new[] { first, second })
                if (process is not null && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            second?.Dispose();
            await DeleteAsync(state);
        }
    }

    // A process the registry started reports its exit status: a request waiting on it fails at once with instance_exited,
    // and starting KiCad again for the project continues the instance ID, naming how the previous process ended.
    [TestMethod]
    public async Task AStartedInstanceReportsItsExitStatusAndContinuesItsIdentity()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("The stand-in executable is a POSIX shell script."); return; }
        string state = Directory.CreateTempSubdirectory("kicad-exit-start-").FullName;
        string project = Path.Combine(state, "project", "exit.kicad_pro");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "{}");
        string executable = Path.Combine(state, "kicad");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var transport = new EchoTransport(project);
        var registry = new InstanceRegistry(transport, state);
        string? id = null;
        var processes = new List<int>();
        try
        {
            var started = await registry.StartInstanceAsync(executable, project);
            id = started.Instance.InstanceId;
            Assert.IsNull(started.Replaced);
            int pid = started.Instance.ProcessId!.Value;
            processes.Add(pid);
            Assert.AreEqual(InstanceProcessStatus.Running, registry.Statuses().Single().State);
            var refused = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.StartInstanceAsync(executable, project));
            Assert.AreEqual("project_owned", refused.Code, "A project whose KiCad still runs is never started twice.");

            var client = registry.Client(id);
            var waiting = client.GetVersionAsync();
            await transport.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var clock = Stopwatch.StartNew();
            using (var process = Process.GetProcessById(pid)) process.Kill();
            var failed = await Assert.ThrowsExactlyAsync<AutomationException>(() => waiting);
            Assert.IsLessThan(10.0, clock.Elapsed.TotalSeconds, "A request in flight fails when the process ends, not after its timeout.");
            Assert.AreEqual("instance_exited", failed.Code);
            foreach (string text in new[] { id, pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "exit status 137 (killed by signal 9, SIGKILL)", "kicad_instance_start" })
                StringAssert.Contains(failed.Message, text);
            var status = registry.Statuses().Single();
            Assert.AreEqual(InstanceProcessStatus.Exited, status.State);
            Assert.AreEqual((137, 9, "SIGKILL", InstanceExit.ExitStatusEvidence), (status.Exit!.ExitCode, status.Exit.Signal, status.Exit.SignalName, status.Exit.Evidence));
            Assert.AreEqual("instance_exited", (await Assert.ThrowsExactlyAsync<AutomationException>(() => client.HandshakeAsync())).Code);

            var again = await registry.StartInstanceAsync(executable, project);
            processes.Add(again.Instance.ProcessId!.Value);
            Assert.AreEqual(id, again.Instance.InstanceId, "The same project continues the ended instance's ID.");
            Assert.AreNotEqual(started.Instance.Epoch, again.Instance.Epoch);
            Assert.AreEqual(started.Instance.Epoch, again.Replaced!.Epoch);
            Assert.AreEqual(137, again.ReplacedExit!.ExitCode);
            Assert.AreEqual(again.Instance.Epoch, registry.Client(id).Epoch);

            // After a server restart, the saved registration's ended process still lets a start continue the ID.
            using (var process = Process.GetProcessById(again.Instance.ProcessId!.Value)) process.Kill();
            await WaitForAsync(() => registry.Statuses().Single().State == InstanceProcessStatus.Exited);
            var restarted = new InstanceRegistry(transport, state);
            var third = await restarted.StartInstanceAsync(executable, project);
            processes.Add(third.Instance.ProcessId!.Value);
            Assert.AreEqual(id, third.Instance.InstanceId);
            Assert.AreEqual(again.Instance.Epoch, third.Replaced!.Epoch);
            Assert.IsNotNull(third.ReplacedExit);
        }
        finally
        {
            // Only the stand-in processes this test started are stopped.
            foreach (int pid in processes)
            {
                try { using var process = Process.GetProcessById(pid); process.Kill(); await process.WaitForExitAsync(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (id is not null)
            {
                string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
                if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
            }
            await DeleteAsync(state);
        }
    }

    // An ended process's exit is also written to the registry in the background, so removing the state
    // folder retries while that write lands.
    private static async Task DeleteAsync(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(directory, true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }

    // Stands in for a KiCad process of one automation instance: its command line carries --automation and the ID.
    private static Process StandIn(string instanceId)
    {
        var start = new ProcessStartInfo("bash") { UseShellExecute = false };
        foreach (string argument in new[] { "-c", "sleep 60; true", "--automation", instanceId }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, limit.Token);
    }

    // Answers the handshake of whichever instance an endpoint belongs to, with a new epoch per endpoint dial sequence;
    // any other request waits until it is cancelled, like a KiCad that never replies.
    private sealed class EchoTransport(string project) : INativeTransport
    {
        private int starts;
        private readonly Dictionary<string, string> epochs = new(StringComparer.Ordinal);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var message = ApiRequest.Parser.ParseFrom(request);
            string id = Path.GetFileName(Path.GetDirectoryName(endpoint["ipc://".Length..])!);
            string header = message.Header.KicadToken;
            string epoch;
            lock (epochs)
            {
                // A request without an epoch comes from a new start's readiness probe: a new process.
                if (header.Length == 0 || !epochs.TryGetValue(id, out epoch!)) epochs[id] = epoch = "epoch-" + Interlocked.Increment(ref starts);
                else if (header != epoch) epoch = header;
            }
            if (!message.Message.Is(GetAutomationSession.Descriptor))
            {
                Waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new ApiResponse
            {
                Header = new ApiResponseHeader { KicadToken = epoch },
                Status = new ApiResponseStatus { Status = ApiStatusCode.AsOk },
                Message = Any.Pack(new AutomationSession { ProtocolVersion = 1, InstanceId = id, ProjectPath = project, Epoch = epoch })
            }.ToByteArray();
        }
    }
}
