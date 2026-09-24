using System.Diagnostics;
using Kiapi.Common.Commands;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyInterruptedStartup(string executable, string display, string temporary,
        string evidence, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(temporary, "interrupted-start")).FullName;
        string project = Path.Combine(directory, "recover.kicad_pro");
        await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", token);
        string state = Path.Combine(directory, "registry");
        var barrier = new StartupHandshakeBarrier();
        var registry = new InstanceRegistry(barrier, state, start =>
        {
            start.Environment["DISPLAY"] = display;
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<InstanceRecord> starting = registry.StartAsync(executable, project, cancellation.Token);
        Process? native = null;
        string? runtime = null;
        try
        {
            // The barrier is reached only after the real process is launched and
            // its receipt is durable, but before any handshake can be accepted.
            Task reached = await Task.WhenAny(barrier.Entered.Task, starting).WaitAsync(token);
            if (reached == starting) await starting; // Surface startup failures without waiting for the barrier.
            await barrier.Entered.Task.WaitAsync(token);
            var pending = await registry.PendingLaunchesAsync(token);
            Assert.AreEqual(1, pending.Count);
            var launch = pending[0];
            Assert.IsNotNull(launch.ProcessId);
            native = Process.GetProcessById(launch.ProcessId.Value);
            runtime = Path.GetDirectoryName(launch.Endpoint["ipc://".Length..])!;
            cancellation.Cancel();
            try { await starting; Assert.Fail("Interrupted startup unexpectedly succeeded."); }
            catch (OperationCanceledException) { Assert.IsTrue(starting.IsCanceled); }
            Assert.AreEqual(0, registry.List().Count);
            Assert.IsFalse(native.HasExited, "Cancelling startup must not terminate KiCad.");

            var recovered = new InstanceRegistry(new NngTransport(), state);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token);
            readiness.CancelAfter(TimeSpan.FromSeconds(15));
            InstanceRecord attached;
            while (true)
            {
                readiness.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(native.HasExited, "KiCad exited before recovery; inspect startup-recovery.native.log.");
                try { attached = await recovered.ReattachAsync(launch.InstanceId, readiness.Token); break; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, readiness.Token);
            }
            Assert.AreEqual(project, attached.ProjectPath);
            Assert.AreEqual(launch.InstanceId, attached.InstanceId);
            Assert.AreEqual(launch.ProcessId, attached.ProcessId);
            Assert.AreEqual(0, (await recovered.PendingLaunchesAsync(token)).Count);
            Assert.IsFalse(native.HasExited);
            var session = await recovered.Client(attached.InstanceId).HandshakeAsync(token);
            Assert.AreEqual(attached.Epoch, session.Epoch);
            // The handshake keeps its named features apart from the requests it handles.
            string[] features = NativeFeatureContracts.Verify(session);
            // A manager with no editor open lists exactly what it dispatches in handled_requests: its
            // own requests and the automation controllers, but no schematic editor request until an
            // editor opens. It opens and closes documents but has no close-all, so that request is
            // neither dispatched nor listed.
            string[] managerOnly = await NativeCapabilityProbe.VerifyHandshakeAsync(recovered.Client(attached.InstanceId),
                Path.Combine(evidence, "startup-recovery.capabilities.json"), token);
            foreach (string type in new[] { GetAutomationSession.Descriptor.FullName, GetVersion.Descriptor.FullName,
                         OpenDocument.Descriptor.FullName, CloseDocument.Descriptor.FullName, CheckedSaveDocument.Descriptor.FullName,
                         CheckedSchematicBatch.Descriptor.FullName })
                CollectionAssert.Contains(managerOnly, type);
            CollectionAssert.DoesNotContain(managerOnly, ReadSchematicScreenData.Descriptor.FullName);
            CollectionAssert.DoesNotContain(managerOnly, CloseAllDocuments.Descriptor.FullName,
                "The desktop manager has no close-all callback, so it must not dispatch or list CloseAllDocuments.");
            // The clean-close journey proves that opening and closing editors leaves them unchanged.
            CollectionAssert.AreEqual(features, (await recovered.Client(attached.InstanceId).HandshakeAsync(token)).Capabilities.ToArray(),
                "A repeated handshake of the same manager must name the same feature contracts.");
            Console.WriteLine($"Manager {attached.InstanceId}: features [{string.Join(", ", features)}], {managerOnly.Length} handled request types.");
            var third = new InstanceRegistry(new NngTransport(), state);
            Assert.AreEqual(0, third.List().Count);
            Assert.AreEqual(attached.InstanceId, (await third.SavedSessionsAsync(token)).Single().InstanceId);
            Assert.AreEqual(attached.Epoch, (await third.ReattachAsync(attached.InstanceId, token)).Epoch);
        }
        finally
        {
            cancellation.Cancel();
            try { await starting; } catch (OperationCanceledException) { }
            // Only the process created by this fixture is disposable. Production
            // StartAsync/reattach never use this teardown path.
            if (native is not null)
            {
                if (!native.HasExited) native.Kill();
                await native.WaitForExitAsync();
                native.Dispose();
            }
            if (runtime is not null && File.Exists(Path.Combine(runtime, "native.log")))
                File.Copy(Path.Combine(runtime, "native.log"), Path.Combine(evidence, "startup-recovery.native.log"), true);
        }
    }

    private sealed class StartupHandshakeBarrier : INativeTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled startup barrier must not return a handshake.");
        }
    }
}
