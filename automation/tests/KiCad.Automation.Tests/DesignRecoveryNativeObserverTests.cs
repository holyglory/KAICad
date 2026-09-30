using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignRecoveryNativeObserverTests
{
    private static async Task Isolated(Func<Fixture, Task> test)
    {
        string root = Directory.CreateTempSubdirectory("kicad-native-intake-").FullName;
        try { using var fixture = new Fixture(root); await test(fixture); }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task InitialSnapshotChangesAndGapRecoveryPreserveDesiredAndBaseline() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer();
        var initial = await observer.ReceiveAsync();
        Assert.IsNotNull(initial.RecoveryRevisionToken);
        Assert.IsFalse(initial.NativeStateChanged);
        Assert.AreEqual(fixture.Saved.RevisionToken, initial.RecoveryRevisionToken);
        Assert.AreEqual(1, fixture.Transport.Snapshots);
        fixture.Source.Send(fixture.Packet(0));
        var attached = await observer.ReceiveAsync();
        Assert.AreEqual(NativeIntakeReason.StreamRecovery, attached.Reason);
        Assert.AreEqual(2, fixture.Transport.Snapshots);

        fixture.Transport.Change("native title", 43);
        fixture.Source.Send(fixture.Packet(0)); // unchanged heartbeat: no snapshot
        fixture.Source.Send(fixture.Packet(1, 43));
        var changed = await observer.ReceiveAsync();
        Assert.IsTrue(changed.NativeStateChanged);
        Assert.AreEqual(NativeIntakeReason.CommittedChange, changed.Reason);
        Assert.AreEqual(3, fixture.Transport.Snapshots);
        Assert.AreEqual("native title", fixture.Store.Read()!.State.Observed.Instances[0].Metadata.TitleBlock.Title);
        fixture.Transport.Change("after missed event", 45);
        fixture.Source.Send(fixture.Packet(1, 43)); // duplicate
        fixture.Source.Send(fixture.Packet(3)); // a missed change advertised by heartbeat
        var recovered = await observer.ReceiveAsync();
        Assert.AreEqual(NativeIntakeReason.StreamRecovery, recovered.Reason);
        Assert.AreEqual(4, fixture.Transport.Snapshots);
        var saved = fixture.Store.Read()!;
        Assert.AreEqual(45UL, saved.State.NativeRevision.Sequence);
        Assert.AreEqual(SchematicDesignXml.Write(fixture.Saved.State.Baseline, fixture.Saved.State.KnowledgeLibraries),
            SchematicDesignXml.Write(saved.State.Baseline, saved.State.KnowledgeLibraries));
        CollectionAssert.AreEqual(fixture.Saved.State.DesiredFileBytes, saved.State.DesiredFileBytes);
        Assert.IsNull(saved.State.PendingMutation);
        Assert.IsFalse(saved.State.TrackingComplete);
    });

    [TestMethod]
    public Task PendingOperationPausesAndCanResumeWithoutAnotherEvent() => Isolated(async fixture =>
    {
        var state = fixture.Saved.State with { PendingMutation = DesignRecoveryStoreTests.Mutation(fixture.Saved.State) };
        var pending = fixture.Store.Save(state, fixture.Saved.RevisionToken);
        using var observer = await fixture.Observer();
        var paused = await observer.ReceiveAsync();
        Assert.AreEqual("pending_recovery_requires_reconciliation", paused.ErrorCode);
        Assert.AreEqual(0, fixture.Transport.Snapshots);
        Assert.AreEqual(state.PendingMutation, fixture.Store.Read()!.State.PendingMutation);
        fixture.Store.Save(state with { PendingMutation = null }, pending.RevisionToken);
        var resumed = await observer.ReceiveAsync();
        Assert.IsNotNull(resumed.RecoveryRevisionToken);
        Assert.AreEqual(1, fixture.Transport.Snapshots);
    });

    [TestMethod]
    public Task SameRevisionEventRestoresConnectivityInvalidatedByHierarchyOnlyRefresh() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        fixture.Source.Send(fixture.Packet(0)); await observer.ReceiveAsync();
        fixture.Transport.Change("independent hierarchy refresh", 43);
        var saved = fixture.Store.Read()!;
        var refreshed = await DesignRecoveryInspector.RefreshAsync(fixture.Store,
            new NativeClient(fixture.Transport, "ipc:///native-intake-fixture.sock", "process-epoch"), saved.RevisionToken);
        Assert.IsNull(refreshed.State.ObservedElectrical);
        fixture.Source.Send(fixture.Packet(1, 43));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var recovered = await observer.ReceiveAsync(deadline.Token);
        Assert.IsNotNull(recovered.RecoveryRevisionToken);
        Assert.AreEqual(43UL, fixture.Store.Read()!.State.ObservedElectrical!.Hierarchy.Revision.Sequence);
        Assert.AreEqual(refreshed.State.Observed, fixture.Store.Read()!.State.Observed);
    });

    [TestMethod]
    public Task ConcurrentXmlSaveIsRetainedWhileNativeObservationRetries() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer();
        fixture.Transport.Change("native edit", 43);
        fixture.Transport.BeforeSnapshot = () =>
        {
            fixture.Transport.BeforeSnapshot = null;
            var saved = fixture.Store.Read()!;
            fixture.Store.Save(saved.State with { DesiredFileBytes = [0xff, 0x3c] }, saved.RevisionToken);
        };
        var observed = await observer.ReceiveAsync();
        Assert.IsNotNull(observed.RecoveryRevisionToken);
        Assert.AreEqual(2, fixture.Transport.Snapshots);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0x3c }, fixture.Store.Read()!.State.DesiredFileBytes);
        var saved = fixture.Store.Read()!;
        Assert.AreEqual(SchematicDesignXml.Write(fixture.Saved.State.Baseline, fixture.Saved.State.KnowledgeLibraries),
            SchematicDesignXml.Write(saved.State.Baseline, saved.State.KnowledgeLibraries));
    });

    [TestMethod]
    public Task SnapshotOlderThanCommitCannotReplaceRecovery() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        fixture.Source.Send(fixture.Packet(0)); await observer.ReceiveAsync();
        fixture.Source.Send(fixture.Packet(1, 43));
        var rejected = await observer.ReceiveAsync();
        Assert.AreEqual("invalid_recovery_revision", rejected.ErrorCode);
        Assert.IsTrue(rejected.ReattachRequired);
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task ChangedDocumentIdentityCannotOverwriteRecovery() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        var packet = fixture.Packet(1, 43); packet.SchematicCommit.Revision.Epoch = "replacement-document";
        fixture.Source.Send(packet);
        var rejected = await observer.ReceiveAsync();
        Assert.AreEqual("native_document_changed", rejected.ErrorCode);
        Assert.IsTrue(rejected.ReattachRequired);
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task ChildSheetEventsRemainInsideTheExactHierarchyScope() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        fixture.Transport.Change("child-sheet edit", 43);
        var child = fixture.Packet(1, 43);
        child.SchematicCommit.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") });
        fixture.Source.Send(child);
        var observed = await observer.ReceiveAsync();
        Assert.IsNotNull(observed.RecoveryRevisionToken);
        Assert.AreEqual(43UL, fixture.Store.Read()!.State.NativeRevision.Sequence);

        var foreignRoot = fixture.Packet(2, 44);
        foreignRoot.SchematicCommit.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
        fixture.Source.Send(foreignRoot);
        Assert.AreEqual("native_document_changed", (await observer.ReceiveAsync()).ErrorCode);
        Assert.AreEqual(43UL, fixture.Store.Read()!.State.NativeRevision.Sequence);
    });

    [TestMethod]
    public Task FailedPersistenceRetainsEventAndDoesNotTrustReturnedMutablePacket() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        fixture.Source.Send(fixture.Packet(0)); await observer.ReceiveAsync();
        fixture.Transport.Change("retained event", 43);
        fixture.Source.Send(fixture.Packet(1, 43));
        DesignRecoveryNativeObservation paused;
        using (var owner = new FileStream(fixture.RecoveryPath + ".lock", FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
            paused = await observer.ReceiveAsync();
        Assert.AreEqual("design_recovery_io", paused.ErrorCode);
        Assert.IsFalse(paused.ReattachRequired);
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        paused.Delivery!.Event.SchematicCommit.Revision.Sequence = 999;
        var recovered = await observer.ReceiveAsync();
        Assert.IsNotNull(recovered.RecoveryRevisionToken);
        Assert.AreEqual(43UL, recovered.Delivery!.Event.SchematicCommit.Revision.Sequence);
        Assert.AreEqual(43UL, fixture.Store.Read()!.State.NativeRevision.Sequence);
        Assert.AreEqual(0, fixture.Transport.Mutations);
    });

    [TestMethod]
    public Task FailedReadCannotBypassRetainedDocumentIdentityOnResume() => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        var packet = fixture.Packet(1, 43); packet.SchematicCommit.Revision.Epoch = "replacement-document";
        fixture.Source.Send(packet);
        using (var owner = new FileStream(fixture.RecoveryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual("design_recovery_io", (await observer.ReceiveAsync()).ErrorCode);
        var rejected = await observer.ReceiveAsync();
        Assert.AreEqual("native_document_changed", rejected.ErrorCode);
        Assert.IsTrue(rejected.ReattachRequired);
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        Assert.AreEqual(1, fixture.Transport.Snapshots);
    });

    [TestMethod]
    [DataRow("instance")]
    [DataRow("process")]
    [DataRow("stream")]
    public Task ChangedStreamIdentityRequiresReattachment(string field) => Isolated(async fixture =>
    {
        using var observer = await fixture.Observer(); await observer.ReceiveAsync();
        var packet = fixture.Packet(0);
        if (field == "instance") packet.InstanceId = Guid.NewGuid().ToString("D");
        else if (field == "process") packet.ProcessEpoch = "replacement-process";
        else packet.EventEpoch = "replacement-stream";
        fixture.Source.Send(packet);
        var rejected = await observer.ReceiveAsync();
        Assert.AreEqual(field == "stream" ? "event_stream_changed" : "instance_changed", rejected.ErrorCode);
        Assert.IsTrue(rejected.ReattachRequired);
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        Assert.AreEqual(1, fixture.Transport.Snapshots);
    });

    [TestMethod]
    public Task CancelledStartupDisposesOnlyTheNewSubscription() => Isolated(async fixture =>
    {
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => DesignRecoveryNativeObserver.CreateAsync(fixture.Store,
            new NativeClient(fixture.Transport, "ipc:///native-intake-fixture.sock", "process-epoch"), _ =>
            { cancelled.Cancel(); return fixture.Source; }, cancelled.Token));
        Assert.IsTrue(fixture.Source.Disposed);
        Assert.AreEqual(0, fixture.Transport.Snapshots);
        Assert.AreEqual(0, fixture.Transport.Mutations);
    });

    [TestMethod]
    public Task OnlyTheCurrentPausedSessionCanResume() => Isolated(async fixture =>
    {
        var state = fixture.Saved.State with { PendingMutation = DesignRecoveryStoreTests.Mutation(fixture.Saved.State) };
        var pending = fixture.Store.Save(state, fixture.Saved.RevisionToken);
        await using var session = new DesignNativeIntakeSession(await fixture.Observer());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await session.WaitAsync(0, deadline.Token);
        Assert.AreEqual(DesignNativeIntakePhase.Paused, paused.Phase);
        Assert.AreEqual("invalid_native_intake_cursor", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            session.WaitAsync(paused.Sequence + 1, deadline.Token))).Code);
        Assert.AreEqual("native_intake_changed", Assert.ThrowsExactly<AutomationException>(() => session.Resume(0)).Code);
        fixture.Store.Save(state with { PendingMutation = null }, pending.RevisionToken);
        session.Resume(paused.Sequence);
        var resumed = await session.WaitAsync(paused.Sequence, deadline.Token);
        Assert.AreEqual(DesignNativeIntakePhase.Watching, resumed.Phase);
        Assert.AreEqual("native_intake_changed", Assert.ThrowsExactly<AutomationException>(() => session.Resume(paused.Sequence)).Code);
        Assert.AreEqual(0, fixture.Transport.Mutations);
    });

    [TestMethod]
    public Task ContinuousSessionRunsBetweenCallsAndStopsOnlyItsObserver() => Isolated(async fixture =>
    {
        var observer = await fixture.Observer();
        await using var session = new DesignNativeIntakeSession(observer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = await session.WaitAsync(0, deadline.Token);
        Assert.AreEqual(DesignNativeIntakePhase.Watching, initial.Phase);
        fixture.Transport.Change("event-driven", 43); fixture.Source.Send(fixture.Packet(1, 43));
        var changed = await session.WaitAsync(initial.Sequence, deadline.Token);
        Assert.IsTrue(changed.Observation!.NativeStateChanged);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitAsync(changed.Sequence, cancelled.Token));
        Assert.AreEqual(DesignNativeIntakePhase.Watching, session.Inspect().Phase);
        await session.DisposeAsync();
        Assert.AreEqual(DesignNativeIntakePhase.Stopped, session.Inspect().Phase);
        Assert.IsTrue(fixture.Source.Disposed);
        Assert.AreEqual(0, fixture.Transport.Mutations);
    });

    [TestMethod]
    public Task HeartbeatsResetSilenceDeadlineWithoutRepeatedSnapshots() => Isolated(async fixture =>
    {
        var observer = await fixture.Observer();
        await using var session = new DesignNativeIntakeSession(observer, TimeSpan.FromMilliseconds(250));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var initial = await session.WaitAsync(0, deadline.Token);
        fixture.Source.Send(fixture.Packet(0));
        var attached = await session.WaitAsync(initial.Sequence, deadline.Token);
        int snapshots = fixture.Transport.Snapshots;
        for (int tick = 0; tick < 8; ++tick)
        {
            fixture.Source.Send(fixture.Packet(0));
            await Task.Delay(60, deadline.Token);
            Assert.AreEqual(DesignNativeIntakePhase.Watching, session.Inspect().Phase);
        }
        Assert.AreEqual(snapshots, fixture.Transport.Snapshots);
        var silent = await session.WaitAsync(attached.Sequence, deadline.Token);
        Assert.AreEqual(DesignNativeIntakePhase.Paused, silent.Phase);
        Assert.AreEqual("native_event_silence", silent.ErrorCode);
        Assert.IsTrue(silent.Observation!.ReattachRequired);
        Assert.ThrowsExactly<AutomationException>(() => session.Resume(silent.Sequence));
    });

    private sealed class Fixture : IDisposable
    {
        public string RecoveryPath { get; }
        public DesignRecoveryStore Store { get; }
        public StoredDesignRecovery Saved { get; }
        public Peer Transport { get; }
        public EventSource Source { get; }
        public Fixture(string root)
        {
            RecoveryPath = Path.Combine(root, "recovery.json");
            Store = new(RecoveryPath);
            var state = DesignRecoveryStoreTests.Fixture();
            state = state with { ObservedElectrical = new() { Hierarchy = new()
            { Data = state.Observed.Clone(), Revision = new() { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence },
                TrackingComplete = state.TrackingComplete } } };
            Saved = Store.Save(state, null);
            Transport = new(Saved.State); Source = new(Transport.Session);
            Transport.Source = Source;
        }
        public Task<DesignRecoveryNativeObserver> Observer() => DesignRecoveryNativeObserver.CreateAsync(Store,
            new NativeClient(Transport, "ipc:///native-intake-fixture.sock", "process-epoch"), _ => { Source.Subscribed = true; return Source; });
        public AutomationEvent Packet(ulong streamSequence, ulong? revision = null)
        {
            var packet = new AutomationEvent { ProtocolVersion = 1, InstanceId = Transport.Session.InstanceId,
                ProcessEpoch = Transport.Session.Epoch, EventEpoch = Transport.Session.EventEpoch,
                Sequence = streamSequence, Heartbeat = new() };
            if (revision is not null) packet.SchematicCommit = new()
            {
                Document = Saved.State.Baseline.Schematic.Document.Clone(),
                Revision = new() { Epoch = Saved.State.NativeRevision.Epoch, Sequence = revision.Value },
                Change = new() { Sequence = revision.Value, Kind = SchematicChange.Types.Kind.Commit }
            };
            return packet;
        }
        public void Dispose() => Source.Dispose();
    }

    private sealed class EventSource(AutomationSession session) : INativeEventSource
    {
        private readonly Channel<AutomationEvent> events = Channel.CreateUnbounded<AutomationEvent>();
        private readonly NativeEventCursor cursor = new(session);
        public bool Subscribed, Disposed;
        public void Send(AutomationEvent packet) => events.Writer.TryWrite(packet);
        public async Task<NativeEventDelivery> ReceiveAsync(CancellationToken token) => cursor.Observe(await events.Reader.ReadAsync(token));
        public void Dispose() { Disposed = true; events.Writer.TryComplete(); }
    }

    private sealed class Peer : INativeTransport
    {
        public AutomationSession Session { get; }
        public SchematicHierarchyDataSnapshot Snapshot { get; }
        public EventSource? Source;
        public Action? BeforeSnapshot;
        public int Snapshots, Mutations;
        public Peer(DesignRecoveryState state)
        {
            Session = new() { ProtocolVersion = 1, InstanceId = state.InstanceId.ToString("D"), Epoch = "process-epoch",
                EventEpoch = "event-epoch", EventEndpoint = "ipc:///native-intake-events.sock" };
            Snapshot = new() { Data = state.Observed.Clone(), TrackingComplete = false,
                Revision = new() { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence } };
        }
        public void Change(string title, ulong sequence)
        {
            Snapshot.Data.Instances[0].Metadata.TitleBlock ??= new();
            Snapshot.Data.Instances[0].Metadata.TitleBlock.Title = title; Snapshot.Revision.Sequence = sequence;
        }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            IMessage response;
            if (message.Is(GetAutomationSession.Descriptor)) response = Session.Clone();
            else if (message.Is(ReadSchematicHierarchyData.Descriptor) || message.Is(ReadSchematicElectricalState.Descriptor))
            {
                Assert.IsTrue(Source!.Subscribed, "Subscribe before capturing the initial native state.");
                ++Snapshots; BeforeSnapshot?.Invoke();
                response = message.Is(ReadSchematicElectricalState.Descriptor)
                    ? new SchematicElectricalState { Hierarchy = Snapshot.Clone() } : Snapshot.Clone();
            }
            else { ++Mutations; throw new AssertFailedException("Native intake must not dispatch mutations."); }
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = "process-epoch" },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(response) }.ToByteArray());
        }
    }
}
