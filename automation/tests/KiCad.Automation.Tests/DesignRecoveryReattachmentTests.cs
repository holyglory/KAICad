using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignRecoveryReattachmentTests
{
    [TestMethod]
    public void ReloadedFormatRequiresXmlPublicationButNeverInventsNativeEdits()
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var old = state.Baseline.Schematic.Clone();
        foreach (var screen in old.Instances)
        {
            screen.Metadata.LoadedNativeFormatVersion = 20250114;
            screen.Metadata.WriterNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion;
        }
        var design = state.Baseline with { Schematic = old };
        var baselineElectrical = state.BaselineElectrical!.Clone(); baselineElectrical.Hierarchy.Data = old.Clone();
        var observed = baselineElectrical.Clone();
        observed.Hierarchy.Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 1 };
        foreach (var screen in observed.Hierarchy.Data.Instances)
            screen.Metadata.LoadedNativeFormatVersion = screen.Metadata.WriterNativeFormatVersion;
        state = state with { Baseline = design, BaselineElectrical = baselineElectrical, ObservedElectrical = observed,
            Observed = observed.Hierarchy.Data.Clone(), NativeRevision = new(observed.Hierarchy.Revision.Epoch, 1),
            DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
        var plan = SchematicSynchronizationPlanner.Plan(state);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorMessage); Assert.IsEmpty(plan.NativeOperations);
        Assert.IsFalse(SchematicSynchronizationExecutor.Equivalent(design, plan.Candidate!, state, default));
        Assert.IsTrue(SchematicSynchronizationExecutor.Equivalent(plan.Candidate!, plan.Candidate!, state, default));
        var wrong = plan.Candidate! with { Schematic = plan.Candidate!.Schematic.Clone() };
        wrong.Schematic.Instances[0].Metadata.WriterNativeFormatVersion++;
        Assert.ThrowsExactly<AutomationException>(() => SchematicSynchronizationExecutor.Equivalent(plan.Candidate, wrong, state, default));
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicSynchronizationExecutor.Equivalent(design, plan.Candidate, state, new(true)));
    }

    [TestMethod]
    public async Task ExplicitNewSessionPreservesBaselineInvalidXmlAndHistoryAndIsRepeatSafe()
    {
        using var f = new Fixture();
        string before = SchematicDesignXml.Write(f.Saved.State.Baseline, f.Saved.State.KnowledgeLibraries);
        var attached = await f.Attach();
        Assert.AreEqual(f.Peer.Observed.State.Revision.Epoch, attached.State.NativeRevision.Epoch);
        Assert.AreEqual(before, SchematicDesignXml.Write(attached.State.Baseline, attached.State.KnowledgeLibraries));
        Assert.AreEqual(f.Saved.State.BaselineElectrical, attached.State.BaselineElectrical);
        CollectionAssert.AreEqual(f.Saved.State.DesiredFileBytes, attached.State.DesiredFileBytes);
        Assert.IsNull(attached.State.OwnershipResolution);
        Assert.AreEqual(f.Peer.Observed.Electrical, attached.State.ObservedElectrical);
        byte[] bytes = File.ReadAllBytes(f.Path);
        var same = await DesignRecoveryReattachment.ReattachAsync(f.Store, f.Client, attached.RevisionToken, attached.State.NativeRevision.Epoch);
        Assert.AreEqual(attached.RevisionToken, same.RevisionToken);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(f.Path));
        await Assert.ThrowsAsync<AutomationException>(() => f.Attach());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(f.Path));
    }

    [TestMethod]
    public async Task OrdinaryObservationCannotAdoptNewSessionAndPendingRequestsCannotBeRebound()
    {
        using var f = new Fixture();
        Assert.AreEqual("invalid_recovery_revision", (await Assert.ThrowsAsync<AutomationException>(() =>
            DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, f.Saved.RevisionToken, includeElectrical: true))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        var pending = f.Store.Save(f.Saved.State with { PendingMutation = DesignRecoveryStoreTests.Mutation(f.Saved.State) }, f.Saved.RevisionToken);
        int calls = f.Peer.Calls;
        Assert.AreEqual("pending_recovery_requires_reconciliation", (await Assert.ThrowsAsync<AutomationException>(() =>
            DesignRecoveryReattachment.ReattachAsync(f.Store, f.Client, pending.RevisionToken, f.Epoch))).Code);
        Assert.AreEqual(calls, f.Peer.Calls); Assert.AreEqual(pending.RevisionToken, f.Store.Read()!.RevisionToken);
    }

    [TestMethod]
    public async Task WrongInstanceDocumentEpochOrCheckpointNeverChangesSavedState()
    {
        foreach (string fault in new[] { "instance", "document", "epoch", "revision", "process", "empty-epoch" })
        {
            using var f = new Fixture();
            string expectedEpoch = f.Epoch;
            switch (fault)
            {
                case "instance": f.Peer.Instance = Guid.NewGuid().ToString("D"); break;
                case "document": f.Peer.Observed.State.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D"); break;
                case "epoch": expectedEpoch = Guid.NewGuid().ToString("D"); break;
                case "revision": f.Peer.Observed.Electrical.Hierarchy.Revision.Sequence++; break;
                case "process": f.Peer.Observed.State.ProcessEpoch = Guid.NewGuid().ToString("D"); break;
                case "empty-epoch": expectedEpoch = ""; break;
            }
            await Assert.ThrowsAsync<AutomationException>(() => DesignRecoveryReattachment.ReattachAsync(
                f.Store, f.Client, f.Saved.RevisionToken, expectedEpoch));
            Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken, fault);
        }
    }

    [TestMethod]
    public async Task ConcurrentWritesCancellationAndWriteFailurePreserveEveryVersion()
    {
        using var f = new Fixture();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => f.Attach(cancelled.Token));
            Assert.AreEqual(0, f.Peer.Calls);
        }
        using (var locked = new FileStream(f.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<AutomationException>(() => f.Attach());
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        using (var cancelled = new CancellationTokenSource())
        {
            f.Peer.BeforeSnapshot = cancelled.Cancel;
            await Assert.ThrowsAsync<OperationCanceledException>(() => f.Attach(cancelled.Token));
            Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        }
        f.Peer.BeforeSnapshot = () => f.Store.Save(f.Saved.State with { DesiredFileBytes = [0xfe] }, f.Saved.RevisionToken);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsAsync<AutomationException>(() => f.Attach())).Code);
        CollectionAssert.AreEqual(new byte[] { 0xfe }, f.Store.Read()!.State.DesiredFileBytes);
        Assert.AreEqual(f.Saved.State.NativeRevision, f.Store.Read()!.State.NativeRevision);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("kicad-reattach-").FullName;
        internal string Path { get; }
        internal DesignRecoveryStore Store { get; }
        internal StoredDesignRecovery Saved { get; }
        internal Peer Peer { get; }
        internal NativeClient Client { get; }
        internal string Epoch => Peer.Observed.State.Revision.Epoch;
        internal Fixture()
        {
            var state = SchematicSynchronizationPlanTests.Fixture() with { DesiredFileBytes = [0xff],
                OwnershipResolution = new(new string('a', 64), Guid.NewGuid(), new string('b', 64)) };
            Path = System.IO.Path.Combine(directory, "recovery.json"); Store = new(Path); Saved = Store.Save(state, null);
            string process = Guid.NewGuid().ToString("D"), epoch = Guid.NewGuid().ToString("D");
            var electrical = state.ObservedElectrical!.Clone(); electrical.Hierarchy.Revision = new() { Epoch = epoch, Sequence = 1 };
            electrical.Hierarchy.Data.Instances[0].Metadata.TitleBlock.Title = "Edited after reload";
            Peer = new(state.InstanceId.ToString("D"), process, new()
            {
                Electrical = electrical,
                State = new() { Document = state.Baseline.Schematic.Document.Clone(), ProcessEpoch = process,
                    NativeIdentity = Guid.NewGuid().ToString("D"), Revision = electrical.Hierarchy.Revision.Clone(),
                    Scope = DocumentLifecycleScope.DlsSchematicHierarchy, ProjectSettingsIncluded = true, StateSha256 = new string('a', 64) }
            });
            Client = new(Peer, "ipc:///tmp/kicad-reattach-fixture.sock", process);
        }
        internal Task<StoredDesignRecovery> Attach(CancellationToken token = default) =>
            DesignRecoveryReattachment.ReattachAsync(Store, Client, Saved.RevisionToken, Epoch, token);
        public void Dispose() => Directory.Delete(directory, true);
    }

    private sealed class Peer(string instance, string process, CheckedSchematicState observed) : INativeTransport
    {
        internal string Instance = instance;
        internal CheckedSchematicState Observed { get; } = observed;
        internal Action? BeforeSnapshot;
        internal int Calls;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            IMessage response;
            if (message.Is(GetAutomationSession.Descriptor)) response = new AutomationSession { ProtocolVersion = 1, InstanceId = Instance, Epoch = process };
            else if (message.Is(ReadCheckedSchematicState.Descriptor)) { BeforeSnapshot?.Invoke(); response = Observed.Clone(); }
            else if (message.Is(ReadSchematicElectricalState.Descriptor)) response = Observed.Electrical.Clone();
            else throw new AssertFailedException("Reattachment must never send a native mutation or save.");
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = process },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(response) }.ToByteArray());
        }
    }
}
