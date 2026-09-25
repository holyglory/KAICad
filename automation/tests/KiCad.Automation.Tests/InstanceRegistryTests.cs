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
                         "exit status 137, the status reported for a process ended by signal 9 (SIGKILL)", "kicad_instance_start" })
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
            // The same instance ID reuses its runtime folder: the ended process's logs moved under its epoch, and the
            // folder's logs now belong to the new process.
            string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
            var kept = Directory.GetDirectories(Path.Combine(runtime, "epochs")).Single();
            CollectionAssert.AreEquivalent(new[] { "bootstrap.stderr.log", "bootstrap.stdout.log" },
                Directory.GetFiles(kept).Select(Path.GetFileName).ToArray());
            Assert.AreEqual(again.Instance.Epoch, (await File.ReadAllTextAsync(Path.Combine(runtime, "logs.epoch"))).Trim());
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "bootstrap.stdout.log")), "The new process writes its own bootstrap log.");

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

    // Review finding 2: an epoch attached to this server answered a handshake, so only the process this server observes
    // for it can prove that it ended. Here the handshake names no process (like a KiCad this server cannot observe, for
    // example in another process ID namespace) and the saved record still carries the process ID of an earlier view of
    // that epoch, which no longer exists: nothing may report the instance as exited, persist an exit, or start a second
    // KiCad for the project. Isolated: no native journey can make a live KiCad's recorded process disappear.
    [TestMethod]
    public async Task AnAttachedEpochThisServerCannotObserveIsNeverProvenExited()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Process observation reads Linux /proc."); return; }
        string state = Directory.CreateTempSubdirectory("kicad-exit-unobserved-").FullName;
        string project = Path.Combine(state, "project", "test.kicad_pro");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "{}");
        var transport = new NativeClientTests.FixtureTransport { ProjectPath = project };
        string id = transport.InstanceId, endpoint = LaunchEndpoint(id);
        try
        {
            using (var earlier = StandIn(id))
            {
                transport.ProcessId = (uint)earlier.Id;
                await new InstanceRegistry(transport, state).AttachAsync(endpoint, id);
                earlier.Kill(entireProcessTree: true);
                await earlier.WaitForExitAsync();
            }
            transport.ProcessId = 0;
            var registry = new InstanceRegistry(transport, state);
            var reattached = await registry.ReattachAsync(id);
            Assert.IsNotNull(reattached.ProcessId, "The saved process ID of this epoch is kept as recorded.");
            Assert.AreEqual(InstanceProcessStatus.Unverified, registry.Statuses().Single().State);
            Assert.IsNull(await registry.ProvenExitAsync(id, "fixture-epoch"), "An attached epoch is never proven exited by a saved process ID.");
            Assert.IsFalse(Directory.Exists(Path.Combine(state, "exits")), "No exit may be recorded for a KiCad that answered.");
            string executable = Path.Combine(state, "kicad");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 1\n");
            var refused = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.StartInstanceAsync(executable, project));
            Assert.AreEqual("project_owned", refused.Code);
            Assert.AreEqual("fixture-epoch", (await registry.Client(id).HandshakeAsync()).Epoch);
        }
        finally { await DeleteAsync(state); }
    }

    // Review findings 2 and 3: after an MCP restart only a saved registration's recorded process identity (boot, process
    // ID namespace, start time) proves how its KiCad ended. A saved KiCad shown still running this instance refuses a
    // start; one proven ended lets the start continue its ID; one this server can neither prove ended nor show running
    // (a record without that identity, or one written in another namespace) starts a new instance ID. Isolated: the
    // native crash journey restarts KiCad while its server keeps observing it, never across a server restart.
    [TestMethod]
    public async Task ASavedRegistrationDecidesAStartOnlyByItsRecordedProcess()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Process identities read Linux /proc."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-exit-saved-").FullName;
        string project = Path.Combine(root, "project", "saved.kicad_pro");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "{}");
        string executable = await StandInExecutable(root);
        var transport = new EchoTransport(project);
        var started = new List<(string Id, int ProcessId)>();
        try
        {
            async Task<string> Saved(string state, int processId, ProcessStartIdentity? identity)
            {
                string id = Guid.NewGuid().ToString("D");
                Directory.CreateDirectory(state);
                await File.WriteAllTextAsync(Path.Combine(state, id + ".json"), JsonSerializer.Serialize(new InstanceRecord(id, project,
                    LaunchEndpoint(id), "saved-epoch", processId, DateTimeOffset.UtcNow, identity)));
                return id;
            }
            async Task<InstanceRegistration> Start(string state)
            {
                var result = await new InstanceRegistry(transport, state).StartInstanceAsync(executable, project);
                started.Add((result.Instance.InstanceId, result.Instance.ProcessId!.Value));
                return result;
            }

            // Shown still running this instance: refused, pointing at reattachment.
            string running = Path.Combine(root, "running");
            string runningId = Guid.NewGuid().ToString("D");
            using (var kicad = StandIn(runningId))
            {
                Directory.CreateDirectory(running);
                await File.WriteAllTextAsync(Path.Combine(running, runningId + ".json"), JsonSerializer.Serialize(new InstanceRecord(runningId, project,
                    LaunchEndpoint(runningId), "saved-epoch", kicad.Id, DateTimeOffset.UtcNow, ProcessIdentity.Record(kicad.Id)!)));
                var refused = await Assert.ThrowsExactlyAsync<AutomationException>(() => new InstanceRegistry(transport, running).StartInstanceAsync(executable, project));
                Assert.AreEqual("project_owned", refused.Code);
                StringAssert.Contains(refused.Message, "kicad_instance_reattach");
                StringAssert.Contains(refused.Message, runningId);

                // Proven ended once that exact process is gone: the start continues the instance ID.
                kicad.Kill(entireProcessTree: true);
                await kicad.WaitForExitAsync();
            }
            var continued = await Start(running);
            Assert.AreEqual(runningId, continued.Instance.InstanceId);
            Assert.AreEqual("saved-epoch", continued.Replaced!.Epoch);
            Assert.AreEqual(InstanceExit.ProcessAbsentEvidence, continued.ReplacedExit!.Evidence);

            // The machine restarted since the record was written: every process of that boot has ended.
            string rebooted = Path.Combine(root, "rebooted");
            var live = ProcessIdentity.Record(Environment.ProcessId)!;
            string rebootedId = await Saved(rebooted, Environment.ProcessId, live with { BootId = Guid.NewGuid().ToString("D") });
            var afterReboot = await Start(rebooted);
            Assert.AreEqual(rebootedId, afterReboot.Instance.InstanceId, "A record from an earlier boot is proven ended.");

            // Neither proven ended nor shown running: a new instance ID, and nothing recorded as exited.
            int gone;
            using (var ended = StandIn(Guid.NewGuid().ToString("D")))
            {
                ended.Kill(entireProcessTree: true);
                await ended.WaitForExitAsync();
                gone = ended.Id;
            }
            foreach (var (name, identity) in new (string, ProcessStartIdentity?)[]
                     { ("legacy", null), ("other-namespace", live with { PidNamespace = live.PidNamespace + 1 }) })
            {
                string state = Path.Combine(root, name);
                string savedId = await Saved(state, gone, identity);
                var fresh = await Start(state);
                Assert.AreNotEqual(savedId, fresh.Instance.InstanceId, $"{name}: an unproven saved registration is not continued.");
                Assert.IsNull(fresh.Replaced, name);
                Assert.IsNull(await new InstanceRegistry(transport, state).ProvenExitAsync(savedId, "saved-epoch"), name);
            }
        }
        finally
        {
            await StopStandIns(started);
            await DeleteAsync(root);
        }
    }

    // Review findings 4 and 5: KiCad started through a launcher that forks it and then ends. The handshake names KiCad's
    // own process, so the launcher's exit is never reported as KiCad's: the server observes the process KiCad names, a
    // second start is refused while it runs, and a request waiting on it fails soon after it ends (the server checks the
    // process while the request waits) instead of after its reply timeout. Isolated: the product KiCad is never started
    // through a launcher here, and the attached-KiCad wait is also proven by the native crash journey.
    [TestMethod]
    public async Task AKiCadStartedThroughALauncherIsObservedByItsOwnProcess()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("The launcher is a POSIX shell script and observation reads /proc."); return; }
        string state = Directory.CreateTempSubdirectory("kicad-exit-launcher-").FullName;
        string project = Path.Combine(state, "project", "launched.kicad_pro");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "{}");
        string executable = Path.Combine(state, "kicad");
        // Starts the stand-in KiCad with KiCad's arguments, records both process IDs, and ends after a few seconds.
        await File.WriteAllTextAsync(executable, "#!/bin/bash\nbash -c 'sleep 60; true' kicad \"$@\" &\n"
            + "echo $! > \"$(dirname \"$0\")/kicad.pid\"\necho $$ > \"$(dirname \"$0\")/launcher.pid\"\nsleep 3\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string kicadPid = Path.Combine(state, "kicad.pid");
        var transport = new EchoTransport(project) { ProcessIdOf = async (_, token) =>
        {
            while (!File.Exists(kicadPid) || (await File.ReadAllTextAsync(kicadPid, token)).Trim().Length == 0) await Task.Delay(20, token);
            return uint.Parse((await File.ReadAllTextAsync(kicadPid, token)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        } };
        var registry = new InstanceRegistry(transport, state);
        var started = new List<(string Id, int ProcessId)>();
        try
        {
            var launched = await registry.StartInstanceAsync(executable, project);
            int kicad = int.Parse((await File.ReadAllTextAsync(kicadPid)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            started.Add((launched.Instance.InstanceId, kicad));
            int launcher = int.Parse((await File.ReadAllTextAsync(Path.Combine(state, "launcher.pid"))).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreNotEqual(launcher, kicad);
            Assert.AreEqual(kicad, launched.Instance.ProcessId, "The registration names the process KiCad named, not the launcher.");
            Assert.IsNotNull(launched.Instance.ProcessStart);
            await WaitForAsync(() => ProcessIdentity.LinuxStartTicks(launcher) is null, TimeSpan.FromSeconds(20));
            Assert.AreEqual(InstanceProcessStatus.Running, registry.Statuses().Single().State, "The launcher ended; KiCad did not.");
            Assert.IsNull(await registry.ProvenExitAsync(launched.Instance.InstanceId, launched.Instance.Epoch));
            Assert.AreEqual("project_owned", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                registry.StartInstanceAsync(executable, project))).Code, "A running KiCad is never started twice.");

            var waiting = registry.Client(launched.Instance.InstanceId).GetVersionAsync();
            await transport.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var clock = Stopwatch.StartNew();
            using (var process = Process.GetProcessById(kicad)) process.Kill(entireProcessTree: true);
            var failed = await Assert.ThrowsExactlyAsync<AutomationException>(() => waiting);
            Assert.IsLessThan(5.0, clock.Elapsed.TotalSeconds, "A request waiting on an attached process fails soon after it ends.");
            Assert.AreEqual("instance_exited", failed.Code);
            StringAssert.Contains(failed.Message, "is no longer running");
            var status = registry.Statuses().Single();
            Assert.AreEqual(InstanceProcessStatus.Exited, status.State);
            Assert.AreEqual((kicad, InstanceExit.ProcessAbsentEvidence), (status.Exit!.ProcessId, status.Exit.Evidence));
        }
        finally
        {
            await StopStandIns(started);
            await DeleteAsync(state);
        }
    }

    // A stand-in KiCad executable: the process it becomes runs until it is stopped and answers nothing itself.
    private static async Task<string> StandInExecutable(string directory)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The stand-in executable is a POSIX shell script.");
        string executable = Path.Combine(directory, "kicad");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
    }

    // Stops only the stand-in processes a test started, and removes their runtime folders.
    private static async Task StopStandIns(IEnumerable<(string Id, int ProcessId)> started)
    {
        foreach (var (id, pid) in started)
        {
            try { using var process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
            if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
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

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? within = null)
    {
        using var limit = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, limit.Token);
    }

    // Answers the handshake of whichever instance an endpoint belongs to, with a new epoch per endpoint dial sequence;
    // any other request waits until it is cancelled, like a KiCad that never replies.
    private sealed class EchoTransport(string project) : INativeTransport
    {
        private int starts;
        private readonly Dictionary<string, string> epochs = new(StringComparer.Ordinal);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The process ID each instance's handshake names; without it, 0 (unknown), like a KiCad built before the field.
        public Func<string, CancellationToken, Task<uint>>? ProcessIdOf { get; init; }

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
            uint processId = ProcessIdOf is { } named ? await named(id, cancellationToken) : 0;
            return new ApiResponse
            {
                Header = new ApiResponseHeader { KicadToken = epoch },
                Status = new ApiResponseStatus { Status = ApiStatusCode.AsOk },
                Message = Any.Pack(new AutomationSession { ProtocolVersion = 1, InstanceId = id, ProjectPath = project, Epoch = epoch,
                    ProcessId = processId })
            }.ToByteArray();
        }
    }
}
