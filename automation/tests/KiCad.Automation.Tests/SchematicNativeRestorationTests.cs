using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNativeRestorationTests
{
    [TestMethod]
    public async Task RestoredOwnersKeepExactIdsAndNewerInstructionsWithoutInventingNets()
    {
        using var fixture = new Fixture(newerInstructions: true);
        var before = File.ReadAllBytes(fixture.Store.StatePath);
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, fixture.Saved);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode + ": " + plan.ErrorMessage);
        var candidate = plan.Candidate!; var original = fixture.Original.Baseline;
        CollectionAssert.AreEquivalent(original.Engineering.Circuit.Components.Select(c => c.Id).ToArray(),
            candidate.Engineering.Circuit.Components.Select(c => c.Id).ToArray());
        CollectionAssert.AreEquivalent(original.SymbolBindings.ToArray(), candidate.SymbolBindings.ToArray());
        CollectionAssert.AreEquivalent(original.Engineering.Circuit.Nets.Select(n => n.Id).ToArray(),
            candidate.Engineering.Circuit.Nets.Select(n => n.Id).ToArray());
        Assert.IsFalse(candidate.Engineering.HasUnresolvedComponentReferences);
        Assert.IsFalse(candidate.Engineering.Structure.HasUnresolvedNetBindings);
        foreach (var statement in fixture.Saved.State.Baseline.Engineering.Structure.Statements)
            Assert.AreEqual(statement.Text, candidate.Engineering.Structure.Statements.Single(s => s.Id == statement.Id).Text);
        Assert.IsTrue(candidate.Engineering.Structure.Statements[0].Text.Contains("New instruction", StringComparison.Ordinal));
        Assert.IsTrue(plan.ObservedConnectivity!.ConnectivityEquivalent); Assert.IsEmpty(plan.NativeOperations);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Store.StatePath));
        Assert.IsTrue(plan.Electrical!.RestoredSymbolOccurrences!.Count > 0);
        Assert.AreEqual(plan.CandidateXml, SchematicDesignXml.Write(SchematicDesignXml.Read(plan.CandidateXml!, fixture.Saved.State.KnowledgeLibraries), fixture.Saved.State.KnowledgeLibraries));
    }

    [TestMethod]
    public async Task RestorationUsesActualNativeGeometryAndRedoRetainsTheLatestText()
    {
        using var fixture = new Fixture(newerInstructions: true);
        var observed = fixture.Saved.State.ObservedElectrical!.Clone();
        foreach (var screen in observed.Hierarchy.Data.Instances)
        for (int i = 0; i < screen.Items.Count; i++)
            if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
            {
                var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                symbol.Position.XNm += 2540000; screen.Items[i] = Any.Pack(symbol);
            }
        var state = fixture.Saved.State with { ObservedElectrical = observed, Observed = observed.Hierarchy.Data.Clone() };
        var saved = fixture.Store.Save(state, fixture.Saved.RevisionToken);
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, saved);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode);
        Assert.IsEmpty(plan.NativeOperations);
        foreach (var symbol in plan.Candidate!.Engineering.Circuit.Symbols)
            Assert.AreEqual(fixture.Original.Baseline.Engineering.Circuit.Symbols.Single(s => s.Id == symbol.Id).Placement!.XMillimeters + 2.54m,
                symbol.Placement!.XMillimeters);
        var settled = state with { Baseline = plan.Candidate, BaselineElectrical = observed.Clone(),
            DesiredFileBytes = Encoding.UTF8.GetBytes(plan.CandidateXml!) };
        var deletion = fixture.DeletedObservation.Clone(); deletion.Hierarchy.Revision.Sequence = observed.Hierarchy.Revision.Sequence + 1;
        var redo = SchematicSynchronizationPlanner.PlanForExecution(settled with { Observed = deletion.Hierarchy.Data.Clone(),
            ObservedElectrical = deletion, NativeRevision = new(deletion.Hierarchy.Revision.Epoch, deletion.Hierarchy.Revision.Sequence) });
        Assert.IsTrue(redo.CanPrepare, redo.ErrorCode); Assert.IsEmpty(redo.Candidate!.Engineering.Circuit.Components);
        Assert.IsTrue(redo.Candidate.Engineering.HasUnresolvedComponentReferences);
        Assert.AreEqual(plan.Candidate.Engineering.Structure.Statements[0].Text, redo.Candidate.Engineering.Structure.Statements[0].Text);
    }

    [TestMethod]
    public async Task ChangedMissingLegacyAndWrongCircuitHistoryCannotRestoreOwners()
    {
        foreach (string failure in new[] { "changed", "missing", "legacy", "circuit" })
        {
            using var fixture = new Fixture();
            var saved = fixture.Saved;
            if (failure == "changed") File.AppendAllText(fixture.Receipt.PreviousXmlPath!, "changed");
            if (failure == "missing") File.Delete(fixture.Receipt.PreviousXmlPath!);
            if (failure == "legacy") saved = fixture.Store.Save(saved.State with
                { LastSynchronization = fixture.Receipt with { Version = 1, PreviousXmlSha256 = null } }, saved.RevisionToken);
            if (failure == "circuit")
            {
                var previous = fixture.Original.Baseline with { Engineering = fixture.Original.Baseline.Engineering with
                    { Circuit = fixture.Original.Baseline.Engineering.Circuit with { Id = Guid.NewGuid() } } };
                byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(previous, saved.State.KnowledgeLibraries));
                File.WriteAllBytes(fixture.Receipt.PreviousXmlPath!, bytes);
                saved = fixture.Store.Save(saved.State with { LastSynchronization = fixture.Receipt with { PreviousXmlSha256 = Hash(bytes) } }, saved.RevisionToken);
            }
            var result = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, saved);
            Assert.IsFalse(result.CanPrepare, failure); Assert.IsNull(result.Candidate); Assert.IsEmpty(result.NativeOperations);
            Assert.AreEqual(saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        }
    }

    [TestMethod]
    public async Task ConcurrentInstructionTextMergesButCircuitEditsStillPauseRestoration()
    {
        using var fixture = new Fixture();
        var current = fixture.Saved.State.Baseline;
        var desired = current with { Engineering = current.Engineering with { Structure = current.Engineering.Structure with
        { Statements = current.Engineering.Structure.Statements.Select(s => s with { Text = s.Text + " New concurrent instruction" }).ToArray() } } };
        var saved = fixture.Store.Save(fixture.Saved.State with
            { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, fixture.Saved.State.KnowledgeLibraries)) }, fixture.Saved.RevisionToken);
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, saved);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode); Assert.IsTrue(plan.Candidate!.Engineering.Structure.Statements[0].Text.EndsWith("New concurrent instruction", StringComparison.Ordinal));
        desired = current with { Engineering = current.Engineering with { Circuit = current.Engineering.Circuit with
        { Parts = current.Engineering.Circuit.Parts.Select(p => p with { Name = p.Name + " pending change" }).ToArray() } } };
        saved = fixture.Store.Save(saved.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, saved.State.KnowledgeLibraries)) }, saved.RevisionToken);
        plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, saved);
        Assert.IsFalse(plan.CanPrepare); Assert.AreEqual("ownership_change_with_xml_edits", plan.ErrorCode);
    }

    [TestMethod]
    public async Task PlanCancellationAndSettledStateDoNotReadOrRewriteHistory()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, fixture.Saved, new(true)));
        var restored = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, fixture.Saved);
        Assert.IsTrue(restored.CanPrepare, restored.ErrorCode);
        var saved = fixture.Store.Save(fixture.Saved.State with { Baseline = restored.Candidate!, BaselineElectrical = fixture.Saved.State.ObservedElectrical!.Clone(),
            DesiredFileBytes = Encoding.UTF8.GetBytes(restored.CandidateXml!) }, fixture.Saved.RevisionToken);
        File.Delete(fixture.Receipt.PreviousXmlPath!);
        var unchanged = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, saved);
        Assert.IsTrue(unchanged.CanPrepare, unchanged.ErrorCode); Assert.AreEqual(restored.CandidateXml, unchanged.CandidateXml);
        Assert.IsNull(unchanged.Electrical!.RestoredSymbolOccurrences);
    }

    [TestMethod]
    public async Task ConflictingVerifiedMappingsAndFutureHistoryAreExplicitFailures()
    {
        using var fixture = new Fixture(newerInstructions: true);
        fixture.AddAlternativeMapping();
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, fixture.Saved);
        Assert.IsFalse(plan.CanPrepare); Assert.AreEqual("ambiguous_native_ownership_history", plan.ErrorCode);
        Assert.IsNull(plan.Candidate); Assert.IsEmpty(plan.NativeOperations);
        using var future = new Fixture();
        var saved = future.Store.Save(future.Saved.State with { LastSynchronization = future.Receipt with
            { NativeRevisionSequence = future.Saved.State.NativeRevision.Sequence + 1 } }, future.Saved.RevisionToken);
        plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(future.Store, saved);
        Assert.AreEqual("native_ownership_history_ahead", plan.ErrorCode);
        // Two causes of missing history keep two codes, and both fail closed. A design never synchronized has no earlier
        // owner to find; a design synchronized without content-verified retained XML may have one that cannot be read,
        // so the restored symbol is never taken for a new one.
        using var never = new Fixture();
        saved = never.Store.Save(never.Saved.State with { LastSynchronization = null }, never.Saved.RevisionToken);
        plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(never.Store, saved);
        Assert.AreEqual("missing_native_ownership_history", plan.ErrorCode);
        Assert.IsNull(plan.Candidate); Assert.IsEmpty(plan.NativeOperations);
        using var unverified = new Fixture();
        saved = unverified.Store.Save(unverified.Saved.State with { LastSynchronization = unverified.Receipt with
            { PreviousXmlPath = null, PreviousXmlSha256 = null } }, unverified.Saved.RevisionToken);
        plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(unverified.Store, saved);
        Assert.AreEqual("unverified_native_ownership_history", plan.ErrorCode);
        Assert.IsNull(plan.Candidate); Assert.IsEmpty(plan.NativeOperations);
    }

    [TestMethod]
    public async Task RemovedCurrentInstructionsAreNotResurrectedFromTheHistoricalDocument()
    {
        using var fixture = new Fixture(newerInstructions: true);
        var current = fixture.Saved.State.Baseline;
        // XML orders stable IDs, not provenance. Remove a leaf so this is a
        // valid user deletion regardless of the fixture's generated UUID order.
        Guid removed = current.Engineering.Structure.Statements.Where(s => !current.Engineering.Structure.Statements
            .Any(other => other.DerivedFrom.Contains(s.Id))).OrderBy(s => s.Id).First().Id;
        var structure = current.Engineering.Structure with
        {
            Statements = current.Engineering.Structure.Statements.Where(s => s.Id != removed).ToArray(),
            UnresolvedNetBindings = current.Engineering.Structure.UnresolvedNetBindings?.Where(r => r.OwnerId != removed).ToArray(),
            UnresolvedComponentReferences = current.Engineering.Structure.UnresolvedComponentReferences?.Where(r => r.OwnerId != removed).ToArray()
        };
        var desired = current with { Engineering = current.Engineering with { Structure = structure } };
        Assert.IsFalse(structure.Statements.Any(s => s.DerivedFrom.Contains(removed)));
        var saved = fixture.Store.Save(fixture.Saved.State with
            { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, fixture.Saved.State.KnowledgeLibraries)) }, fixture.Saved.RevisionToken);
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(fixture.Store, saved);
        Assert.IsTrue(plan.CanPrepare, plan.ErrorCode); Assert.IsFalse(plan.Candidate!.Engineering.Structure.Statements.Any(s => s.Id == removed));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("native-owner-history-").FullName;
        internal DesignRecoveryState Original { get; }
        internal KiCad.Automation.Protocol.SchematicElectricalState DeletedObservation { get; }
        internal DesignRecoveryStore Store { get; }
        internal StoredDesignRecovery Saved { get; }
        internal DesignSynchronizationReceipt Receipt { get; }
        internal Fixture(bool newerInstructions = false)
        {
            var initial = SchematicSynchronizationPlanTests.Fixture();
            string epoch = Guid.NewGuid().ToString("D");
            initial.BaselineElectrical!.Hierarchy.Revision.Epoch = epoch;
            initial.ObservedElectrical!.Hierarchy.Revision.Epoch = epoch;
            Original = initial with { NativeRevision = new(epoch, initial.NativeRevision.Sequence) };
            var deleted = Original.ObservedElectrical!.Clone(); deleted.Hierarchy.Revision.Sequence++;
            foreach (var screen in deleted.Hierarchy.Data.Instances)
                foreach (var symbol in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).ToArray()) screen.Items.Remove(symbol);
            deleted.Nets.Clear(); DeletedObservation = deleted;
            var state = Original with { Observed = deleted.Hierarchy.Data.Clone(), ObservedElectrical = deleted,
                NativeRevision = new(epoch, deleted.Hierarchy.Revision.Sequence) };
            var removal = SchematicSynchronizationPlanner.PlanForExecution(state); Assert.IsTrue(removal.CanPrepare, removal.ErrorCode);
            byte[] before = Original.DesiredFileBytes;
            string designPath = Path.Combine(directory, "design.xml"); Guid operation = Guid.NewGuid();
            Receipt = new(2, operation, state.InstanceId, designPath, new string('a', 64), Hash(Encoding.UTF8.GetBytes(removal.CandidateXml!)),
                Guid.NewGuid().ToString("D"), epoch, deleted.Hierarchy.Revision.Sequence, false, true, null,
                designPath + ".sync-" + operation.ToString("N"), Hash(before));
            File.WriteAllBytes(Receipt.PreviousXmlPath!, before);
            Store = new(Path.Combine(directory, "recovery.json"));
            var baseline = removal.Candidate!;
            DesignSynchronizationReceipt latest = Receipt;
            if (newerInstructions)
            {
                new DesignSynchronizationReceipts(Store.StatePath).Archive(Receipt);
                baseline = baseline with { Engineering = baseline.Engineering with { Structure = baseline.Engineering.Structure with
                    { Statements = baseline.Engineering.Structure.Statements.Select(s => s with { Text = s.Text + " New instruction after deletion" }).ToArray() } } };
                latest = Receipt with { OperationId = Guid.NewGuid(), PreviousXmlPath = null, PreviousXmlSha256 = null,
                    DesignFileSha256 = Hash(Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, state.KnowledgeLibraries))) };
            }
            var observed = Original.ObservedElectrical!.Clone(); observed.Hierarchy.Revision.Sequence = deleted.Hierarchy.Revision.Sequence + 1;
            byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, state.KnowledgeLibraries)); File.WriteAllBytes(designPath, xml);
            Saved = Store.Save(state with { Baseline = baseline, BaselineElectrical = deleted.Clone(), DesiredFileBytes = xml,
                Observed = observed.Hierarchy.Data.Clone(), ObservedElectrical = observed, NativeRevision = new(epoch, observed.Hierarchy.Revision.Sequence),
                LastSynchronization = latest }, null);
        }
        internal DesignSynchronizationReceipt AddAlternativeMapping()
        {
            var old = Original.Baseline; Guid former = old.Engineering.Circuit.Components[0].Id, replacement = Guid.NewGuid();
            Guid Map(Guid id) => id == former ? replacement : id;
            var alternative = old with { Engineering = old.Engineering with
            {
                Circuit = old.Engineering.Circuit with
                {
                    Components = old.Engineering.Circuit.Components.Select(c => c with { Id = Map(c.Id) }).ToArray(),
                    Symbols = old.Engineering.Circuit.Symbols.Select(s => s with { ComponentId = Map(s.ComponentId) }).ToArray(),
                    Nets = old.Engineering.Circuit.Nets.Select(n => n with { Pins = n.Pins.Select(p => p with { ComponentId = Map(p.ComponentId) }).ToArray() }).ToArray()
                },
                ComponentBindings = old.Engineering.ComponentBindings.Select(b => b with { ComponentInstanceId = Map(b.ComponentInstanceId) }).ToArray(),
                Structure = old.Engineering.Structure with
                {
                    Blocks = old.Engineering.Structure.Blocks.Select(b => b with { ComponentIds = b.ComponentIds.Select(Map).ToArray() }).ToArray(),
                    Statements = old.Engineering.Structure.Statements.Select(s => s with { TargetId = Map(s.TargetId),
                        Connection = s.Connection is not { } c ? null : c with
                        { First = c.First with { ComponentId = Map(c.First.ComponentId) }, Second = c.Second with { ComponentId = Map(c.Second.ComponentId) } } }).ToArray()
                }
            } };
            Guid id = Guid.NewGuid(); byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(alternative, Saved.State.KnowledgeLibraries));
            var receipt = Receipt with { OperationId = id, PreviousXmlPath = Receipt.DesignPath + ".sync-" + id.ToString("N"), PreviousXmlSha256 = Hash(bytes) };
            File.WriteAllBytes(receipt.PreviousXmlPath, bytes);
            new DesignSynchronizationReceipts(Store.StatePath).Archive(receipt);
            return receipt;
        }

        public void Dispose() => Directory.Delete(directory, true);
    }
}
