using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Protocol = KiCad.Automation.Protocol;
using Session = KiCad.Automation.Protocol.AutomationSession;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class AutomaticDesignSynchronizationTests
{
    [TestMethod]
    public async Task BusyObservationWaitsForAnEventAndIdleHeartbeatsDoNotRefreshDesigns()
    {
        using var driver = new Driver { RefreshError = new NativeApiException(7, "native edit in progress") };
        await using var session = new AutomaticDesignSynchronization(driver);
        var waiting = await Until(session, s => s.Phase == AutomaticDesignPhase.WaitingForEditor);
        Assert.AreEqual("native_busy", waiting.ErrorCode);
        Assert.AreEqual(1, driver.RefreshCount); Assert.AreEqual(0, driver.Applies);
        await driver.ObservedReasons.Reader.ReadAsync();
        driver.RefreshError = null;
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.Heartbeat));
        await Until(session, s => s.Phase == AutomaticDesignPhase.Watching);
        Assert.AreEqual(2, driver.RefreshCount); Assert.AreEqual(1, driver.Applies);
        await driver.ObservedReasons.Reader.ReadAsync();
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.Heartbeat));
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.File));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.AreEqual(AutomaticDesignSignal.File, await driver.ObservedReasons.Reader.ReadAsync(deadline.Token));
        Assert.AreEqual(3, driver.RefreshCount);
    }

    [TestMethod]
    public async Task BusyPendingOperationKeepsItsIdentityAndWaitCanBeStopped()
    {
        using var fixture = new DesignPublicationRecoveryTests.Fixture();
        var pending = fixture.Store.Save(fixture.Saved.State with { PendingPublication = fixture.Intent with
            { RequestedRecoveryRevisionToken = fixture.Saved.RevisionToken } }, fixture.Saved.RevisionToken);
        using var driver = new Driver(fixture.Store) { ApplyError = new NativeApiException(7, "busy") };
        await using var session = new AutomaticDesignSynchronization(driver);
        var waiting = await Until(session, s => s.Phase == AutomaticDesignPhase.WaitingForEditor);
        Assert.AreEqual(fixture.Intent.OperationId, waiting.OperationId);
        Assert.AreEqual(pending.RevisionToken, fixture.Store.Read()!.RevisionToken);
        driver.Inputs.Writer.TryWrite(new(AutomaticDesignSignal.Heartbeat));
        await Until(session, s => s.Phase == AutomaticDesignPhase.WaitingForEditor && driver.Applies == 2);
        Assert.AreEqual(fixture.Intent.OperationId, driver.LastOperation);
        Assert.AreEqual(fixture.Saved.RevisionToken, driver.LastRequest);
        Assert.AreEqual(2, driver.Applies);
        Assert.AreEqual(pending.RevisionToken, fixture.Store.Read()!.RevisionToken,
            "A still-busy retry must not fabricate completion or discard a pending receipt.");
        await session.DisposeAsync();
        Assert.AreEqual(pending.RevisionToken, fixture.Store.Read()!.RevisionToken);

        using var busyDriver = new Driver { RefreshError = new NativeApiException(7, "busy") };
        await using var busy = new AutomaticDesignSynchronization(busyDriver);
        await Until(busy, s => s.Phase == AutomaticDesignPhase.WaitingForEditor);
        await busy.DisposeAsync();
        Assert.IsTrue(busyDriver.Disposed);
        Assert.AreEqual(AutomaticDesignPhase.Stopped, busy.Inspect().Phase);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(6)]
    public async Task NonBusyNativeErrorsStillPause(int status)
    {
        using var driver = new Driver { RefreshError = new NativeApiException(status, "not a temporary edit") };
        await using var session = new AutomaticDesignSynchronization(driver);
        var paused = await Until(session, s => s.Phase == AutomaticDesignPhase.Paused);
        Assert.AreEqual("native_status_" + status, paused.ErrorCode);
        Assert.AreEqual(0, driver.Applies);
    }

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

    // Decision n39ac0ccc5c9270f2 / ledger pbfcccd896f17cf27: the kicad_design_sync_plan preview, the automatic
    // worker and apply each hand planning the recorded instance's handshake, so a connection-only XML revision
    // is classified identically by all three once KiCad advertises schematic.connection-realization.v1, and
    // exactly as today without such a handshake. This drives the real preview tool, worker and executor entry
    // points with a scripted handshake: no KiCad build advertises the capability yet (CN-1 §8.3), so no rendered
    // journey can reach the advertised branch. Every expectation for it is derived from the planner's own
    // reference plan, so the check holds for whichever planner is built in: one that refuses the connection
    // before KiCad is touched, or lane 2A's, which plans a realization that apply measures in the captured
    // checkpoint before anything is journaled or sent (CN-1 §9.1). The native journeys keep covering the
    // unchanged path.
    [TestMethod]
    public async Task PreviewWorkerAndApplyClassifyAConnectionOnlyRevisionFromTheSameHandshake()
    {
        string directory = Directory.CreateTempSubdirectory("handshake-planning-").FullName;
        try
        {
            var state = SchematicSynchronizationPlanTests.Fixture();
            // An editor checkpoint apply can accept: one exact native revision, and every sheet reporting the
            // project's connection grid that realization draws on (CN-1 §6.1). The saved baseline, the observation
            // and both electrical checkpoints carry the same settings, so no side sees a formatting change.
            string nativeEpoch = Guid.NewGuid().ToString("D"), processEpoch = Guid.NewGuid().ToString("D");
            var schematic = state.Baseline.Schematic.Clone();
            foreach (var screen in schematic.Instances) screen.Metadata.Formatting = SchematicFormattingTests.Formatting();
            var nativeRevision = new Protocol.DocumentRevision { Epoch = nativeEpoch, Sequence = state.NativeRevision.Sequence };
            var baselineElectrical = state.BaselineElectrical!.Clone();
            baselineElectrical.Hierarchy.Data = schematic.Clone(); baselineElectrical.Hierarchy.Revision = nativeRevision.Clone();
            var observedElectrical = state.ObservedElectrical!.Clone();
            observedElectrical.Hierarchy.Data = schematic.Clone(); observedElectrical.Hierarchy.Revision = nativeRevision.Clone();
            state = state with { Baseline = state.Baseline with { Schematic = schematic }, Observed = schematic.Clone(),
                NativeRevision = new(nativeEpoch, nativeRevision.Sequence), BaselineElectrical = baselineElectrical,
                ObservedElectrical = observedElectrical };
            var circuit = state.Baseline.Engineering.Circuit;
            Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id;
            var design = state.Baseline with { Engineering = state.Baseline.Engineering with { Circuit = circuit with
                { Nets = [.. circuit.Nets, new CircuitNet(Guid.NewGuid(), "SIG", [new(u1, "1"), new(u2, "1")])] } } };
            byte[] desired = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries));
            state = state with { DesiredFileBytes = desired };
            Session Handshake(Guid instance, bool realization)
            {
                var session = new Session { ProtocolVersion = 1, InstanceId = instance.ToString("D"), Epoch = processEpoch };
                session.Capabilities.Add("session.info");
                if (realization) session.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
                return session;
            }
            var advertising = Handshake(state.InstanceId, realization: true);
            Assert.AreEqual(SchematicConnectedAdditionKind.Admitted,
                SchematicConnectedAddition.Classify(state, DesignRecoveryStore.ReadDesired(state), advertising).Kind,
                "The fixture revision only adds a connection over drawn pins.");
            string Recovery()
            {
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N"), "recovery.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                new DesignRecoveryStore(path).Save(state, null);
                return path;
            }
            var offline = new RefusingTransport();
            async Task<JsonElement> Preview(Session? recorded)
            {
                string recovery = Recovery();
                var tools = new RecoveryTools(new InstanceRegistry(offline, Path.Combine(directory, "registry")),
                    handshakes: recorded is null ? null : new AttachedHandshakes(id => id == state.InstanceId.ToString("D") ? recorded : null));
                var result = await tools.PlanSynchronization(state.InstanceId.ToString("D"), recovery,
                    new DesignRecoveryStore(recovery).Read()!.RevisionToken, default);
                return JsonSerializer.SerializeToElement(result.StructuredContent);
            }
            // The worker is held inside apply, so the phase it applies in is observed rather than inferred.
            async Task<(AutomaticDesignStatus First, AutomaticDesignStatus? WhileApplying, AutomaticDesignStatus Settled, int Applies)> Worker(Session? session)
            {
                using var driver = new Driver(new DesignRecoveryStore(Recovery())) { Session = session,
                    ApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
                    ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                await using var sync = new AutomaticDesignSynchronization(driver);
                var first = await Until(sync, s => s.Phase is AutomaticDesignPhase.Applying or AutomaticDesignPhase.Paused);
                AutomaticDesignStatus? applying = null;
                if (first.Phase == AutomaticDesignPhase.Applying)
                {
                    await driver.ApplyEntered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    applying = sync.Inspect();
                    Assert.AreEqual(driver.LastOperation, applying.OperationId, "The worker applies the operation it announced.");
                    driver.ApplyGate!.TrySetResult();
                }
                var settled = await Until(sync, s => s.Phase is AutomaticDesignPhase.Watching or AutomaticDesignPhase.Paused);
                return (first, applying, settled, driver.Applies);
            }
            // Apply against a scripted editor that answers its handshake and, when asked to, the checkpoint
            // capture; every other request is refused. Journaled reports whether apply wrote its recovery journal.
            async Task<(string? Code, IReadOnlyList<string> Requests, bool Journaled)> Apply(Session live, bool answerCapture)
            {
                var store = new DesignRecoveryStore(Recovery());
                string before = store.Read()!.RevisionToken;
                string designPath = Path.Combine(Path.GetDirectoryName(store.StatePath)!, "design.xml");
                await File.WriteAllBytesAsync(designPath, desired);
                var checkpoint = new Protocol.CheckedSchematicState
                {
                    Electrical = observedElectrical.Clone(),
                    State = new() { Document = state.Baseline.Schematic.Document.Clone(), ProcessEpoch = live.Epoch,
                        NativeIdentity = Guid.NewGuid().ToString("D"), Revision = nativeRevision.Clone(),
                        Scope = Protocol.DocumentLifecycleScope.DlsSchematicHierarchy, ProjectSettingsIncluded = true,
                        StateSha256 = new string('a', 64) }
                };
                string nativeFile = Path.Combine(Path.GetDirectoryName(store.StatePath)!, "fixture.kicad_sch");
                checkpoint.State.NativeFiles.Add(nativeFile);
                checkpoint.State.FileBaselines.Add(new Protocol.NativeFileBaselineState { Path = nativeFile, BaselinePath = nativeFile,
                    BaselineKnown = true, BaselineExists = true, BaselineSha256 = new string('b', 64), BaselineBytes = 1,
                    CurrentKnown = true, CurrentExists = true, CurrentSha256 = new string('b', 64), CurrentBytes = 1,
                    Status = Protocol.NativeFileBaselineStatus.NfbsUnchanged });
                var editor = new ScriptedEditor(live, answerCapture ? checkpoint : null);
                string? code = null;
                try
                {
                    await SchematicSynchronizationExecutor.ApplyAsync(store, new NativeClient(editor, "ipc:///handshake-fixture.sock", live.Epoch),
                        designPath, before, Guid.NewGuid());
                    Assert.Fail("The scripted editor cannot complete an application.");
                }
                catch (AutomationException error) { code = error.Code; }
                catch (NativeApiException) { }
                return (code, editor.Requests, store.Read()!.RevisionToken != before);
            }
            string handshakeRequest = Protocol.GetAutomationSession.Descriptor.FullName;
            string captureRequest = Protocol.ReadCheckedSchematicState.Descriptor.FullName;
            string measurementRequest = Protocol.MeasureSchematicPlacement.Descriptor.FullName;

            // With the capability all three follow the planner's reference plan for this handshake.
            var reference = SchematicSynchronizationPlanner.Plan(state, advertising);
            Assert.IsTrue(SchematicSynchronizationPlanner.Plan(state).CanPrepare, "Without a handshake the general path prepares it.");
            Assert.IsTrue(reference.CanPrepare || reference.ErrorCode is not null, "A refused plan names its reason.");
            Assert.IsTrue(!reference.CanPrepare || reference.NativeConnectionRealizationRequired,
                "An admitted connection-only revision either stops at its planning code or plans a native realization (CN-1 §4.3).");
            var preview = await Preview(advertising);
            Assert.AreEqual(reference.CanPrepare, preview.GetProperty("canPrepare").GetBoolean(), preview.GetRawText());
            Assert.AreEqual(reference.ErrorCode, preview.GetProperty("errorCode").GetString(), preview.GetRawText());
            // The preview is the reference plan, including whether it realizes and the connections it would draw (CN-1 §9.5).
            McpProcessTests.RequirePreviewPlan(reference, preview);
            var automatic = await Worker(advertising);
            if (reference.CanPrepare)
            {
                Assert.AreEqual(AutomaticDesignPhase.Applying, automatic.First.Phase, automatic.First.ErrorCode);
                Assert.AreEqual(AutomaticDesignPhase.Applying, automatic.WhileApplying?.Phase);
                Assert.AreEqual(AutomaticDesignPhase.Watching, automatic.Settled.Phase, automatic.Settled.ErrorCode);
                Assert.AreEqual(1, automatic.Applies, "The worker hands the realization plan to apply exactly once.");
            }
            else
            {
                Assert.AreEqual(AutomaticDesignPhase.Paused, automatic.First.Phase);
                Assert.AreEqual(reference.ErrorCode, automatic.First.ErrorCode);
                Assert.AreEqual(0, automatic.Applies, "The worker must not hand an unrealizable plan to apply.");
            }
            var applied = await Apply(advertising, answerCapture: true);
            Assert.AreEqual(handshakeRequest, applied.Requests.FirstOrDefault(), "Apply starts with its own handshake.");
            Assert.IsFalse(applied.Journaled, "Nothing is journaled before the editor has been measured.");
            if (!reference.CanPrepare)
            {
                Assert.AreEqual(reference.ErrorCode, applied.Code);
                CollectionAssert.AreEqual(new[] { handshakeRequest }, applied.Requests.ToArray(),
                    "Apply refuses after its handshake, before capturing or changing the editor.");
            }
            else
            {
                // CN-1 §9.1: the checkpoint is captured and checked first, then the realization measures it. The
                // scripted editor refuses the measurement, so nothing is journaled, applied or saved.
                CollectionAssert.AreEqual(new[] { handshakeRequest, captureRequest }, applied.Requests.Take(2).ToArray(), string.Join(", ", applied.Requests));
                Assert.AreEqual(measurementRequest, applied.Requests.ElementAtOrDefault(2),
                    "After the capture the realization measures the checkpoint, not the general path: " + string.Join(", ", applied.Requests));
                Assert.IsTrue(applied.Requests.Skip(2).All(r => r == measurementRequest), string.Join(", ", applied.Requests));
            }

            // Without it (no attachment, today's editor, or another instance's handshake): today's general plan.
            var unchanged = await Preview(null);
            Assert.IsTrue(unchanged.GetProperty("canPrepare").GetBoolean(), unchanged.GetRawText());
            string Stable(JsonElement plan) => JsonSerializer.Serialize(plan.EnumerateObject()
                .Where(p => p.Name != "recoveryRevisionToken").ToDictionary(p => p.Name, p => p.Value));
            foreach (var (name, recorded) in new (string, Session)[]
                { ("today's editor", Handshake(state.InstanceId, realization: false)), ("another instance", Handshake(Guid.NewGuid(), realization: true)) })
                Assert.AreEqual(Stable(unchanged), Stable(await Preview(recorded)), name);
            foreach (var session in new Session?[] { null, Handshake(state.InstanceId, realization: false), Handshake(Guid.NewGuid(), realization: true) })
            {
                var general = await Worker(session);
                Assert.AreEqual(AutomaticDesignPhase.Applying, general.WhileApplying?.Phase, general.First.ErrorCode);
                Assert.AreEqual(AutomaticDesignPhase.Watching, general.Settled.Phase, general.Settled.ErrorCode);
                Assert.AreEqual(1, general.Applies);
            }
            var today = await Apply(Handshake(state.InstanceId, realization: false), answerCapture: false);
            Assert.IsNull(today.Code);
            Assert.IsFalse(today.Journaled);
            CollectionAssert.AreEqual(new[] { handshakeRequest, captureRequest },
                today.Requests.ToArray(), "Without the capability apply plans the general path and goes on to capture the editor.");
            // The scripted checkpoint is one apply accepts: on the general path it passes every staleness and file
            // check and is journaled, and today's editor is never measured for a realization.
            var accepted = await Apply(Handshake(state.InstanceId, realization: false), answerCapture: true);
            CollectionAssert.AreEqual(new[] { handshakeRequest, captureRequest }, accepted.Requests.Take(2).ToArray(), string.Join(", ", accepted.Requests));
            Assert.IsTrue(accepted.Journaled, accepted.Code + ": " + string.Join(", ", accepted.Requests));
            CollectionAssert.DoesNotContain(accepted.Requests.ToArray(), measurementRequest);
            Assert.AreEqual(0, offline.Requests, "The preview never contacts KiCad.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RefusingTransport : INativeTransport
    {
        internal int Requests;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Requests);
            throw new AssertFailedException("Planning must not contact KiCad.");
        }
    }

    /// <summary>An editor that answers only its handshake and, when given one, the checked checkpoint capture.
    /// Every other request is refused and recorded by its full message name.</summary>
    private sealed class ScriptedEditor(Session session, Protocol.CheckedSchematicState? checkpoint) : INativeTransport
    {
        internal List<string> Requests { get; } = [];
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            Requests.Add(message.TypeUrl[(message.TypeUrl.LastIndexOf('/') + 1)..]);
            IMessage? reply = message.Is(Protocol.GetAutomationSession.Descriptor) ? session
                : checkpoint is not null && message.Is(Protocol.ReadCheckedSchematicState.Descriptor) ? checkpoint.Clone() : null;
            var response = new ApiResponse { Header = new() { KicadToken = session.Epoch },
                Status = new() { Status = reply is null ? (ApiStatusCode)3 : (ApiStatusCode)1, ErrorMessage = reply is null ? "Refused by the scripted editor" : "" } };
            if (reply is not null) response.Message = Any.Pack(reply);
            return Task.FromResult(response.ToByteArray());
        }
    }

    private static async Task<AutomaticDesignStatus> Until(AutomaticDesignSynchronization session, Func<AutomaticDesignStatus, bool> accept)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var current = session.Inspect();
        while (!accept(current))
        {
            Assert.AreNotEqual(AutomaticDesignPhase.Stopped, current.Phase, current.ErrorMessage);
            current = await session.WaitAsync(current.Sequence, timeout.Token);
            if (!accept(current) && current.Phase == AutomaticDesignPhase.Paused)
                Assert.Fail("Unexpected synchronization pause: " + current.ErrorCode + ": " + current.ErrorMessage);
        }
        return current;
    }

    private sealed class Driver : IAutomaticDesignDriver, IDisposable
    {
        private readonly string? directory;
        public DesignRecoveryStore Store { get; }
        public KiCad.Automation.Protocol.AutomationSession? Session { get; init; }
        internal Channel<AutomaticDesignInput> Inputs { get; } = Channel.CreateUnbounded<AutomaticDesignInput>();
        internal Channel<int> Refreshes { get; } = Channel.CreateUnbounded<int>();
        internal Channel<AutomaticDesignSignal> ObservedReasons { get; } = Channel.CreateUnbounded<AutomaticDesignSignal>();
        internal int Applies, RefreshCount;
        internal Guid LastOperation;
        internal string? LastRequest;
        internal Exception? ApplyError;
        internal Exception? RefreshError;
        internal bool Disposed;
        // Set when apply is entered; while the gate is closed apply stays in progress.
        internal TaskCompletionSource? ApplyEntered { get; init; }
        internal TaskCompletionSource? ApplyGate { get; init; }
        internal Driver(DesignRecoveryStore? store = null)
        {
            if (store is not null) { Store = store; return; }
            directory = Directory.CreateTempSubdirectory("automatic-sync-").FullName;
            Store = new(Path.Combine(directory, "recovery.json")); Store.Save(SchematicSynchronizationPlanTests.Fixture(), null);
        }
        public Task<AutomaticDesignInput> ReceiveAsync(CancellationToken token) => Inputs.Reader.ReadAsync(token).AsTask();
        public Task<StoredDesignRecovery> RefreshAsync(AutomaticDesignInput input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Refreshes.Writer.TryWrite(++RefreshCount);
            ObservedReasons.Writer.TryWrite(input.Reasons);
            return RefreshError is { } error ? Task.FromException<StoredDesignRecovery>(error) : Task.FromResult(Store.Read()!);
        }
        public Task<SchematicSynchronizationExecution> ApplyAsync(StoredDesignRecovery saved, Guid operationId, string requestRevisionToken, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Applies++; LastOperation = operationId; LastRequest = requestRevisionToken;
            ApplyEntered?.TrySetResult();
            return ApplyGate is { } gate ? Held(gate.Task) : Result();
            async Task<SchematicSynchronizationExecution> Held(Task open) { await open.WaitAsync(token); return await Result(); }
            Task<SchematicSynchronizationExecution> Result() => ApplyError is { } error
                ? Task.FromException<SchematicSynchronizationExecution>(error)
                : Task.FromResult(new SchematicSynchronizationExecution(saved.RevisionToken, new string('a', 64), saved.State.NativeRevision,
                    false, false, true, null, PublicationId: operationId));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Dispose() { if (directory is not null) Directory.Delete(directory, true); }
    }
}
