using System.Threading.Channels;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class AutomaticDesignSynchronizationTests
{
    [TestMethod]
    public async Task InitialSynchronizationAndSettledFeedbackDoNotRepeatApplication()
    {
        using var driver = new Driver();
        await using var session = new AutomaticDesignSynchronization(driver);
        await Until(session, s => s.Phase == AutomaticDesignPhase.Watching);
        Assert.AreEqual(1, driver.Applies);
        await driver.Refreshes.Reader.ReadAsync();
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.File | AutomaticDesignSignal.Native));
        await driver.Refreshes.Reader.ReadAsync();
        await session.DisposeAsync();
        Assert.AreEqual(1, driver.Applies); Assert.IsTrue(driver.Disposed);
        Assert.AreEqual(AutomaticDesignPhase.Stopped, session.Inspect().Phase);
    }

    [TestMethod]
    public async Task InvalidXmlIsPreservedAndACorrectedSaveAutomaticallyResumes()
    {
        using var driver = new Driver();
        var saved = driver.Store.Read()!; byte[] valid = saved.State.DesiredFileBytes;
        driver.Store.Save(saved.State with { DesiredFileBytes = [0xff] }, saved.RevisionToken);
        await using var session = new AutomaticDesignSynchronization(driver);
        await Until(session, s => s.Phase == AutomaticDesignPhase.InvalidDesign);
        Assert.AreEqual(0, driver.Applies); CollectionAssert.AreEqual(new byte[] { 0xff }, driver.Store.Read()!.State.DesiredFileBytes);
        saved = driver.Store.Read()!; driver.Store.Save(saved.State with { DesiredFileBytes = valid }, saved.RevisionToken);
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.File));
        await Until(session, s => s.Phase == AutomaticDesignPhase.Watching);
        Assert.AreEqual(1, driver.Applies);
    }

    [TestMethod]
    public async Task ApplyFailurePausesWithoutRepeatedRetriesUntilExplicitCurrentResume()
    {
        using var driver = new Driver { ApplyError = new AutomationException("write_failed", "fixture persistence failure") };
        await using var session = new AutomaticDesignSynchronization(driver);
        var paused = await Until(session, s => s.Phase == AutomaticDesignPhase.Paused);
        Assert.AreEqual("write_failed", paused.ErrorCode); Assert.AreEqual(1, driver.Applies);
        Assert.ThrowsExactly<AutomationException>(() => session.Resume(paused.Sequence + 1));
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.File));
        driver.ApplyError = null; session.Resume(paused.Sequence);
        await Until(session, s => s.Phase == AutomaticDesignPhase.Watching);
        Assert.AreEqual(2, driver.Applies);
    }

    [TestMethod]
    public async Task PendingRecoveryUsesItsOriginalOperationAndRequestIdentity()
    {
        using var fixture = new DesignPublicationRecoveryTests.Fixture();
        var pending = fixture.Store.Save(fixture.Saved.State with { PendingPublication = fixture.Intent with
            { RequestedRecoveryRevisionToken = fixture.Saved.RevisionToken } }, fixture.Saved.RevisionToken);
        using var driver = new Driver(fixture.Store) { ApplyError = new AutomationException("native_temporarily_unavailable", "fixture") };
        await using var session = new AutomaticDesignSynchronization(driver);
        await Until(session, s => s.Phase == AutomaticDesignPhase.Paused);
        Assert.AreEqual(fixture.Intent.OperationId, driver.LastOperation);
        Assert.AreEqual(fixture.Saved.RevisionToken, driver.LastRequest);
        Assert.AreEqual(0, driver.RefreshCount);
        Assert.AreEqual(pending.RevisionToken, fixture.Store.Read()!.RevisionToken);
    }

    [TestMethod]
    public async Task ObservationFailureRequiresReattachmentAndWaitCancellationDoesNotStopWorker()
    {
        using var driver = new Driver();
        await using var session = new AutomaticDesignSynchronization(driver);
        var watching = await Until(session, s => s.Phase == AutomaticDesignPhase.Watching);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitAsync(watching.Sequence, new(true)));
        Assert.AreEqual(AutomaticDesignPhase.Watching, session.Inspect().Phase);
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.Recovery, ReattachRequired: true, ErrorCode: "stream_lost"));
        var paused = await Until(session, s => s.Phase == AutomaticDesignPhase.Paused);
        Assert.IsTrue(paused.ReattachRequired);
        Assert.AreEqual("automatic_sync_reattach_required", Assert.ThrowsExactly<AutomationException>(() => session.Resume(paused.Sequence)).Code);
    }

    private static async Task<AutomaticDesignStatus> Until(AutomaticDesignSynchronization session, Func<AutomaticDesignStatus, bool> accept)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var current = session.Inspect();
        while (!accept(current))
        {
            Assert.AreNotEqual(AutomaticDesignPhase.Stopped, current.Phase, current.ErrorMessage);
            current = await session.WaitAsync(current.Sequence, timeout.Token);
        }
        return current;
    }

    private sealed class Driver : IAutomaticDesignDriver, IDisposable
    {
        private readonly string? directory;
        public DesignRecoveryStore Store { get; }
        internal Channel<AutomaticDesignInput> Inputs { get; } = Channel.CreateUnbounded<AutomaticDesignInput>();
        internal Channel<int> Refreshes { get; } = Channel.CreateUnbounded<int>();
        internal int Applies, RefreshCount;
        internal Guid LastOperation;
        internal string? LastRequest;
        internal Exception? ApplyError;
        internal bool Disposed;
        internal Driver(DesignRecoveryStore? store = null)
        {
            if (store is not null) { Store = store; return; }
            directory = Directory.CreateTempSubdirectory("automatic-sync-").FullName;
            Store = new(Path.Combine(directory, "recovery.json")); Store.Save(SchematicSynchronizationPlanTests.Fixture(), null);
        }
        public Task<AutomaticDesignInput> ReceiveAsync(CancellationToken token) => Inputs.Reader.ReadAsync(token).AsTask();
        public Task<StoredDesignRecovery> RefreshAsync(AutomaticDesignInput input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Refreshes.Writer.TryWrite(++RefreshCount); return Task.FromResult(Store.Read()!);
        }
        public Task<SchematicSynchronizationExecution> ApplyAsync(StoredDesignRecovery saved, Guid operationId, string requestRevisionToken, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Applies++; LastOperation = operationId; LastRequest = requestRevisionToken;
            if (ApplyError is { } error) return Task.FromException<SchematicSynchronizationExecution>(error);
            return Task.FromResult(new SchematicSynchronizationExecution(saved.RevisionToken, new string('a', 64), saved.State.NativeRevision,
                false, false, true, null, PublicationId: operationId));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Dispose() { if (directory is not null) Directory.Delete(directory, true); }
    }
}
