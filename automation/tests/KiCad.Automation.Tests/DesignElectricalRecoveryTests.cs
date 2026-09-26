using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using SchematicHierarchyData = Kiapi.Schematic.Types.SchematicHierarchyData;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignElectricalRecoveryTests
{
    private static async Task Isolated(Func<Fixture, Task> test)
    {
        string root = Directory.CreateTempSubdirectory("electrical-recovery-").FullName;
        try { await test(new(root)); }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task LegacyAndElectricalCheckpointsRoundTripWithoutChurn() => Isolated(f =>
    {
        Assert.IsNull(f.Saved.State.BaselineElectrical); Assert.IsNull(f.Saved.State.ObservedElectrical);
        Assert.AreEqual(1, JsonNode.Parse(File.ReadAllText(f.Path))!["Version"]!.GetValue<int>());
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Save(f.Store.Read()!.State, f.Saved.RevisionToken).RevisionToken);
        var state = f.Saved.State with { BaselineElectrical = f.Peer.Electrical.Clone(), ObservedElectrical = f.Peer.Electrical.Clone() };
        var saved = f.Store.Save(state, f.Saved.RevisionToken);
        Assert.AreEqual(3, JsonNode.Parse(File.ReadAllText(f.Path))!["Version"]!.GetValue<int>());
        Assert.AreEqual(state.BaselineElectrical, f.Store.Read()!.State.BaselineElectrical);
        Assert.AreEqual(state.ObservedElectrical, f.Store.Read()!.State.ObservedElectrical);
        Assert.AreEqual(saved.RevisionToken, f.Store.Save(f.Store.Read()!.State, saved.RevisionToken).RevisionToken);
        CollectionAssert.AreEqual(f.Saved.State.DesiredFileBytes, f.Store.Read()!.State.DesiredFileBytes);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task MismatchedAndUnknownCheckpointFieldsCannotReplaceRecovery() => Isolated(f =>
    {
        var valid = f.Saved.State with { BaselineElectrical = f.Peer.Electrical.Clone(), ObservedElectrical = f.Peer.Electrical.Clone() };
        foreach (string kind in new[] { "hierarchy", "revision", "tracking", "future", "baseline-future" })
        {
            var state = valid with { BaselineElectrical = valid.BaselineElectrical!.Clone(), ObservedElectrical = valid.ObservedElectrical!.Clone() };
            if (kind == "hierarchy") state.ObservedElectrical!.Hierarchy.Data.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
            else if (kind == "revision") state.ObservedElectrical!.Hierarchy.Revision.Sequence++;
            else if (kind == "tracking") state.ObservedElectrical!.Hierarchy.TrackingComplete = true;
            else if (kind == "baseline-future") state.BaselineElectrical!.Hierarchy.Revision.Sequence++;
            else state.ObservedElectrical!.MergeFrom(new byte[] { 0x98, 0x06, 0x01 }); // unknown field99
            Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(state, f.Saved.RevisionToken), kind);
            Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ElectricalEnvelopeRejectsDuplicateNestedFieldsAndWrongVersion() => Isolated(f =>
    {
        f.Store.Save(f.Saved.State with { ObservedElectrical = f.Peer.Electrical }, f.Saved.RevisionToken);
        string original = File.ReadAllText(f.Path);
        string duplicated = original.Replace("\"Epoch\":\"electrical-epoch\"", "\"Epoch\":\"electrical-epoch\",\"Epoch\":\"electrical-epoch\"", StringComparison.Ordinal);
        Assert.AreNotEqual(original, duplicated);
        File.WriteAllText(f.Path, duplicated);
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Read());
        var node = JsonNode.Parse(original)!; node["Version"] = 1;
        File.WriteAllText(f.Path, node.ToJsonString());
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Read());
        File.WriteAllText(f.Path, original);
        Assert.IsNotNull(f.Store.Read()!.State.ObservedElectrical);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task InitializeRequiresModelAndNativeAgreementAndPreservesBothBaselines() => Isolated(async f =>
    {
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken);
        Assert.AreEqual(f.Peer.Electrical, initialized.State.BaselineElectrical);
        Assert.AreEqual(initialized.State.BaselineElectrical, initialized.State.ObservedElectrical);
        Assert.AreEqual(f.BaselineXml, SchematicDesignXml.Write(initialized.State.Baseline, initialized.State.KnowledgeLibraries));
        CollectionAssert.AreEqual(f.Saved.State.DesiredFileBytes, initialized.State.DesiredFileBytes);
        Assert.AreEqual("electrical_baseline_exists", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, initialized.RevisionToken))).Code);
        f.Peer.Electrical.Nets.Clear(); f.Peer.Electrical.Hierarchy.Revision.Sequence++;
        var refreshed = await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, initialized.RevisionToken, includeElectrical: true);
        Assert.IsEmpty(refreshed.State.ObservedElectrical!.Nets);
        Assert.IsNotEmpty(refreshed.State.BaselineElectrical!.Nets);
        Assert.AreEqual(initialized.State.BaselineElectrical, refreshed.State.BaselineElectrical);
        Assert.AreEqual(f.BaselineXml, SchematicDesignXml.Write(refreshed.State.Baseline, refreshed.State.KnowledgeLibraries));
        Assert.AreEqual(refreshed.RevisionToken, (await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, refreshed.RevisionToken, includeElectrical: true)).RevisionToken);
    });

    [TestMethod]
    public Task InitializationNeverHidesPendingOperationsOrWrongConnectivity() => Isolated(async f =>
    {
        f.Peer.Electrical.Nets.Clear();
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        var pending = DesignRecoveryStoreTests.Mutation(f.Saved.State);
        var saved = f.Store.Save(f.Saved.State with { PendingMutation = pending }, f.Saved.RevisionToken);
        int calls = f.Peer.Calls;
        Assert.AreEqual("pending_recovery_requires_reconciliation", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, saved.RevisionToken))).Code);
        Assert.AreEqual(calls, f.Peer.Calls); Assert.AreEqual(pending, f.Store.Read()!.State.PendingMutation);
    });

    [TestMethod]
    public Task InitializationDoesNotReplaceChangedHierarchyOrConcurrentXml() => Isolated(async f =>
    {
        f.Peer.Electrical.Hierarchy.Data.Instances[0].Metadata.TextVariables.Add("changed", "native");
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        f.Peer.Electrical.Hierarchy.Data = f.Saved.State.Observed.Clone();
        f.Peer.BeforeSnapshot = () => f.Store.Save(f.Saved.State with { DesiredFileBytes = [0xfe] }, f.Saved.RevisionToken);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        CollectionAssert.AreEqual(new byte[] { 0xfe }, f.Store.Read()!.State.DesiredFileBytes);
        Assert.IsNull(f.Store.Read()!.State.BaselineElectrical);
    });

    // Ledger p91fda8ca22a68141: preview 23's snapshots listed library_cache among the state they could not hold; this build's
    // snapshot of the same schematic no longer does, because it holds the library cache exactly. A record saved by preview
    // 23 without an electrical baseline must still establish one against the upgraded KiCad: that retired marker names the
    // old reader's limits, not the design. Every real difference in the schematic, the library cache included, and any other
    // coverage difference is still refused. The
    // NativeXmlRebuild journey proves the same record through the public tools against a live KiCad; this isolates the
    // comparison rule against the scripted peer, which the journey cannot vary (it would have to edit KiCad itself).
    // Extends no existing test: the neighbouring cases assert a refusal, this one the absence of a spurious one.
    [TestMethod]
    public Task AnEarlierPreviewsCoverageListIsNotANativeEdit() => Isolated(async f =>
    {
        string[] upgraded = ["complete_project_settings", "shared_screen_root_ownership", "net_chains"];
        string[] preview23 = ["complete_project_settings", "shared_screen_root_ownership", "library_cache", "net_chains"];
        static SchematicHierarchyData Listing(SchematicHierarchyData data, string[] list)
        {
            var result = data.Clone();
            foreach (var screen in result.Instances) { screen.Metadata.UnrepresentedState.Clear(); screen.Metadata.UnrepresentedState.Add(list); }
            return result;
        }
        f.Peer.Electrical.Hierarchy.Data = Listing(f.Peer.Electrical.Hierarchy.Data, upgraded);
        var written = f.Saved.State.Baseline with { Schematic = Listing(f.Saved.State.Baseline.Schematic, preview23) };
        var old = f.Store.Save(f.Saved.State with { Baseline = written, Observed = Listing(f.Saved.State.Observed, preview23) }, f.Saved.RevisionToken);
        Assert.AreEqual(1, JsonNode.Parse(File.ReadAllText(f.Path))!["Version"]!.GetValue<int>(), "A record without electrical checkpoints.");
        string writtenXml = SchematicDesignXml.Write(written, old.State.KnowledgeLibraries);

        // Must-catch: a schematic that really differs is refused however its coverage list reads: a text variable, and a
        // library cache the saved snapshot did not hold.
        f.Peer.Electrical.Hierarchy.Data.Instances[0].Metadata.TextVariables.Add("changed", "native");
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, old.RevisionToken))).Code);
        f.Peer.Electrical.Hierarchy.Data.Instances[0].Metadata.TextVariables.Remove("changed");
        var cached = f.Peer.Electrical.Hierarchy.Data.Instances[0].CachedSymbols;
        cached.Add(new Kiapi.Schematic.Types.SchematicCachedSymbol { CacheKey = "Device:R_extra", Definition = new() });
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, old.RevisionToken))).Code);
        cached.RemoveAt(cached.Count - 1);
        // Only the retired library_cache marker is exempt: a snapshot that names other state it cannot hold is refused too.
        f.Peer.Electrical.Hierarchy.Data.Instances[0].Metadata.UnrepresentedState.Add("future_settings");
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, old.RevisionToken))).Code);
        f.Peer.Electrical.Hierarchy.Data.Instances[0].Metadata.UnrepresentedState.Remove("future_settings");
        Assert.AreEqual(old.RevisionToken, f.Store.Read()!.RevisionToken, "A refusal writes nothing.");
        var differs = Listing(written.Schematic, upgraded);
        differs.Instances[0].Metadata.TextVariables.Add("changed", "native");
        Assert.IsFalse(DesignRecoveryInspector.SameSchematicContent(written.Schematic, differs));

        // The same schematic under the upgraded reader's list: the baseline is established and the saved design is kept as
        // preview 23 wrote it, for the next synchronization to publish with the current list.
        Assert.IsTrue(DesignRecoveryInspector.SameSchematicContent(written.Schematic, f.Peer.Electrical.Hierarchy.Data));
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, old.RevisionToken);
        Assert.AreEqual(writtenXml, SchematicDesignXml.Write(initialized.State.Baseline, initialized.State.KnowledgeLibraries));
        Assert.AreEqual(written.Schematic, initialized.State.BaselineElectrical!.Hierarchy.Data, "The baseline checkpoint belongs to the saved baseline.");
        CollectionAssert.AreEqual(f.Peer.Electrical.Nets.ToArray(), initialized.State.BaselineElectrical.Nets.ToArray());
        Assert.AreEqual(f.Peer.Electrical, initialized.State.ObservedElectrical, "The observation is KiCad's own, with its current list.");
        Assert.AreEqual(f.Peer.Electrical.Hierarchy.Data, initialized.State.Observed);
        CollectionAssert.AreEqual(f.Saved.State.DesiredFileBytes, initialized.State.DesiredFileBytes);
        var reread = f.Store.Read()!;
        Assert.AreEqual(initialized.RevisionToken, reread.RevisionToken);
        Assert.AreEqual(initialized.State.BaselineElectrical, reread.State.BaselineElectrical);
        Assert.AreEqual(3, JsonNode.Parse(File.ReadAllText(f.Path))!["Version"]!.GetValue<int>(), "The record gained its electrical checkpoints.");
    });

    // Ledger p91fda8ca22a68141, review of 31cbe4f594: a record preview 23 really wrote, not one this build's store writes with
    // library_cache put back. automation/tests/fixtures/preview-23-recovery holds, byte for byte, a settled recovery record
    // (record version 3: electrical checkpoints, nothing pending) and its design file (design XML version 1; the record's
    // DesiredFileBytes). Native-acceptance run t20260924T160501Z-67b01b captured them on 3f023fc2aa, the source of preview 23
    // (check journey-native-xml-component-creation, 0bc6ddd7-596f-46bd-ba81-5f424c085ce0-realization-join-anchor-label.recovery.json).
    // Its design is hierarchical, with one sheet file shown by two sheets. Whatever the record and design formats become, this
    // file must still read, plan and save as it did. The NativeXmlRebuild journey proves against a live KiCad that this build's
    // snapshot of such a schematic differs from preview 23's only by the retired library_cache marker; here that snapshot is the
    // saved observation without it. Unit test, because no journey can run preview 23 itself; it extends no existing test, since
    // every other record in the suites is written by this build's store.
    [TestMethod]
    public void ARecordPreview23WroteReadsAndPlansAgainstThisBuildsSnapshot()
    {
        string fixture = Path.Combine(PsuCpuFixture.RepositoryRoot, "automation", "tests", "fixtures", "preview-23-recovery");
        string root = Directory.CreateTempSubdirectory("preview-23-recovery-").FullName;
        try
        {
            string path = Path.Combine(root, "recovery.json");
            File.Copy(Path.Combine(fixture, "settled.recovery.json"), path);
            byte[] design = File.ReadAllBytes(Path.Combine(fixture, "settled.design.xml"));
            Assert.AreEqual(3, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>(), "Electrical checkpoints, nothing pending.");
            var store = new DesignRecoveryStore(path);
            var saved = store.Read()!;
            var state = saved.State;
            Assert.IsFalse(state.HasPendingWork);
            CollectionAssert.AreEqual(design, state.DesiredFileBytes, "The design file is the one the record saved.");
            string designXml = new UTF8Encoding(false, true).GetString(design);
            StringAssert.StartsWith(designXml, "<design version=\"1\" ");
            Assert.AreEqual(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries),
                SchematicDesignXml.Write(SchematicDesignXml.Read(designXml, state.KnowledgeLibraries), state.KnowledgeLibraries),
                "The design file holds the saved baseline.");
            // What makes it preview 23's: every snapshot, saved and observed, lists library_cache third of four.
            string[] preview23 = ["complete_project_settings", "shared_screen_root_ownership", "library_cache", "net_chains"];
            Assert.HasCount(3, state.Observed.Instances);
            foreach (var screen in state.Baseline.Schematic.Instances.Concat(state.Observed.Instances))
                CollectionAssert.AreEqual(preview23, screen.Metadata.UnrepresentedState.ToArray());
            Assert.AreEqual(state.Baseline.Schematic, state.Observed, "Settled: KiCad showed the saved design.");
            // Saving it again with this build's store writes the same bytes: upgrading does not churn a record.
            Assert.AreEqual(saved.RevisionToken, store.Save(state, saved.RevisionToken).RevisionToken);

            // This build's snapshot of the same schematic is the same content and needs no native edit, on every sheet.
            var upgraded = state.Observed.Clone();
            foreach (var screen in upgraded.Instances) Assert.IsTrue(screen.Metadata.UnrepresentedState.Remove("library_cache"));
            Assert.IsTrue(DesignRecoveryInspector.SameSchematicContent(state.Baseline.Schematic, upgraded));
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(upgraded, state.Baseline.Schematic));
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(state.Baseline.Schematic, upgraded));
            for (int i = 0; i < upgraded.Instances.Count; ++i)
            {
                Assert.IsEmpty(SchematicItemDelta.Plan(upgraded.Instances[i], state.Baseline.Schematic.Instances[i]));
                Assert.IsEmpty(SchematicItemDelta.Plan(state.Baseline.Schematic.Instances[i], upgraded.Instances[i]));
            }
            // Must-catch on the same record: any other coverage difference is still a change KiCad cannot make.
            var dropped = upgraded.Clone();
            foreach (var screen in dropped.Instances) Assert.IsTrue(screen.Metadata.UnrepresentedState.Remove("net_chains"));
            Assert.IsFalse(DesignRecoveryInspector.SameSchematicContent(state.Baseline.Schematic, dropped));
            Assert.AreEqual("unsupported_schematic_delta", Assert.ThrowsExactly<AutomationException>(() =>
                SchematicItemDelta.Plan(dropped.Instances[0], state.Baseline.Schematic.Instances[0])).Code);
            // A rebuild would restore its library caches (every placed symbol's definition is cached under KiCad's key), and
            // still refuses the sheet file shown twice, which no snapshot holds.
            Assert.IsFalse(SchematicRebuild.Lost("library_cache", state.Baseline.Schematic));
            Assert.IsTrue(SchematicRebuild.Lost("shared_screen_root_ownership", state.Baseline.Schematic));

            // Reattached to this build's KiCad, it plans normally: nothing to send, and the design as this build writes it.
            var observedElectrical = state.ObservedElectrical!.Clone();
            observedElectrical.Hierarchy.Data = upgraded.Clone();
            var reattached = state with { Observed = upgraded, ObservedElectrical = observedElectrical };
            var plan = SchematicSynchronizationPlanner.Plan(reattached);
            Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
            Assert.IsEmpty(plan.NativeOperations, "Nothing to send to KiCad.");
            Assert.IsFalse(plan.NativeRebuildRequired);
            Assert.IsFalse(plan.NativeConnectionRealizationRequired);
            Assert.AreEqual(SchematicDesignXml.Write(state.Baseline with { Schematic = upgraded }, state.KnowledgeLibraries), plan.CandidateXml);
            // The same record saved before electrical checkpoints stops at planning and names the one action that fixes it.
            var legacy = SchematicSynchronizationPlanner.Plan(reattached with { BaselineElectrical = null });
            Assert.AreEqual("missing_electrical_baseline", legacy.ErrorCode, legacy.ErrorMessage);
            StringAssert.Contains(legacy.ErrorMessage, "kicad_design_electrical_baseline_initialize");
            Assert.IsNull(legacy.Candidate);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task HierarchyOnlyRefreshInvalidatesStaleElectricalObservationButNotBaseline() => Isolated(async f =>
    {
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken);
        var same = await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, initialized.RevisionToken);
        Assert.AreEqual(initialized.RevisionToken, same.RevisionToken);
        f.Peer.Electrical.Hierarchy.Revision.Sequence++;
        var changed = await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, same.RevisionToken);
        Assert.IsNull(changed.State.ObservedElectrical);
        Assert.AreEqual(initialized.State.BaselineElectrical, changed.State.BaselineElectrical);
    });

    [TestMethod]
    public Task FailedWriteCancellationAndWrongInstancePreserveOriginalRecovery() => Isolated(async f =>
    {
        using (var held = new FileStream(f.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual("design_recovery_io", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store,
            f.Client, f.Saved.RevisionToken, new(true)));
        f.Peer.Instance = Guid.NewGuid().ToString("D");
        Assert.AreEqual("recovery_instance_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
    });

    private sealed class Fixture
    {
        public string Path { get; }
        public DesignRecoveryStore Store { get; }
        public StoredDesignRecovery Saved { get; }
        public Peer Peer { get; }
        public NativeClient Client { get; }
        public string BaselineXml { get; }
        public Fixture(string root)
        {
            var f = SchematicElectricalComparisonTests.Fixture();
            Path = System.IO.Path.Combine(root, "recovery.json"); Store = new(Path);
            var instance = Guid.NewGuid(); BaselineXml = SchematicDesignXml.Write(f.Design, [f.Library]);
            Saved = Store.Save(new(Guid.NewGuid(), instance, new(f.State.Hierarchy.Revision.Epoch, f.State.Hierarchy.Revision.Sequence),
                false, f.Design, [0xff, 0x3c], f.State.Hierarchy.Data.Clone(), [f.Library]), null);
            Peer = new(f.State.Clone(), instance.ToString("D"));
            Client = new(Peer, "ipc:///tmp/electrical-recovery.sock", "process-epoch");
        }
    }

    private sealed class Peer(SchematicElectricalState electrical, string instance) : INativeTransport
    {
        public SchematicElectricalState Electrical { get; } = electrical;
        public string Instance { get; set; } = instance;
        public Action? BeforeSnapshot;
        public int Calls;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); ++Calls;
            var message = ApiRequest.Parser.ParseFrom(request).Message; IMessage response;
            if (message.Is(GetAutomationSession.Descriptor)) response = new AutomationSession { ProtocolVersion = 1, InstanceId = Instance, Epoch = "process-epoch" };
            else if (message.Is(ReadSchematicElectricalState.Descriptor)) { BeforeSnapshot?.Invoke(); response = Electrical.Clone(); }
            else if (message.Is(ReadSchematicHierarchyData.Descriptor)) { BeforeSnapshot?.Invoke(); response = Electrical.Hierarchy.Clone(); }
            else throw new AssertFailedException("Recovery must never mutate the native peer.");
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = "process-epoch" },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(response) }.ToByteArray());
        }
    }
}
