using System.Text;
using System.Text.Json.Nodes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignPublicationRecoveryTests
{
    [TestMethod]
    [DataRow(DesignPublicationPhase.Staged)]
    [DataRow(DesignPublicationPhase.Attempting)]
    [DataRow(DesignPublicationPhase.Published)]
    public async Task ReopenResumesTheSamePublicationWithoutSwappingBack(DesignPublicationPhase interruption)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExactlyAsync<IOException>(() => DesignPublicationCommitter.CommitAsync(
            fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt, afterPhase: phase =>
            { if (phase == interruption) throw new IOException("Interrupted after a durable phase"); }));
        var reopened = new DesignRecoveryStore(fixture.RecordPath);
        var pending = reopened.Read()!;
        Assert.AreEqual(interruption, pending.State.PendingPublication!.Phase);
        var result = await DesignPublicationCommitter.CommitAsync(reopened, pending.RevisionToken, fixture.SaveReceipt);
        Assert.AreEqual(DesignPublicationPhase.Published, result.Recovery.State.PendingPublication!.Phase);
        CollectionAssert.AreEqual(fixture.Intent.CandidateFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        CollectionAssert.AreEqual(fixture.Intent.ExpectedFileBytes, await File.ReadAllBytesAsync(fixture.Intent.PreviousPath));
        Assert.AreEqual(fixture.Intent.OperationId, result.Recovery.State.PendingPublication.OperationId);
        Assert.AreEqual(SchematicDesignXml.Write(fixture.Saved.State.Baseline, fixture.Saved.State.KnowledgeLibraries),
            SchematicDesignXml.Write(result.Recovery.State.Baseline, result.Recovery.State.KnowledgeLibraries),
            "File publication cannot advance the design baseline.");
        byte[] record = await File.ReadAllBytesAsync(fixture.RecordPath);
        var repeat = await DesignPublicationCommitter.CommitAsync(reopened, result.Recovery.RevisionToken, fixture.SaveReceipt);
        Assert.AreEqual(result.Recovery.RevisionToken, repeat.Recovery.RevisionToken);
        CollectionAssert.AreEqual(record, await File.ReadAllBytesAsync(fixture.RecordPath));
        CollectionAssert.AreEqual(fixture.Intent.CandidateFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
    }

    [TestMethod]
    public async Task InterruptionAfterNativeReplacementIsRecognizedBeforeAnotherReplacement()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExactlyAsync<IOException>(() => DesignPublicationCommitter.CommitAsync(
            fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt,
            afterReplacement: () => throw new IOException("Lost result immediately after replacement")));
        var pending = fixture.Store.Read()!;
        Assert.AreEqual(DesignPublicationPhase.Attempting, pending.State.PendingPublication!.Phase);
        var result = await DesignPublicationCommitter.CommitAsync(new(fixture.RecordPath), pending.RevisionToken, fixture.SaveReceipt,
            afterReplacement: () => Assert.Fail("A completed file swap must never be repeated."));
        Assert.IsTrue(result.ReplacementPerformed);
        CollectionAssert.AreEqual(fixture.Intent.CandidateFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        CollectionAssert.AreEqual(fixture.Intent.ExpectedFileBytes, await File.ReadAllBytesAsync(fixture.Intent.PreviousPath));
    }

    [TestMethod]
    public async Task ConcurrentUserSaveIsRetainedAndCannotBecomeASuccessfulResume()
    {
        using var fixture = new Fixture();
        byte[] newer = "<newer-user-save/>"u8.ToArray();
        var failure = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignPublicationCommitter.CommitAsync(
            fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt,
            afterPhase: phase => { if (phase == DesignPublicationPhase.Attempting) File.WriteAllBytes(fixture.Intent.DesignPath, newer); }));
        Assert.AreEqual("publication_conflict_preserved", failure.Code);
        CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        var pending = fixture.Store.Read()!;
        await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignPublicationCommitter.CommitAsync(
            fixture.Store, pending.RevisionToken, fixture.SaveReceipt));
        CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        Assert.AreEqual(SchematicDesignXml.Write(fixture.Saved.State.Baseline, fixture.Saved.State.KnowledgeLibraries),
            SchematicDesignXml.Write(fixture.Store.Read()!.State.Baseline, fixture.Saved.State.KnowledgeLibraries));
    }

    [TestMethod]
    public async Task SaveRacingTheSystemCallSurvivesAtItsRecordedPathAfterReopen()
    {
        using var fixture = new Fixture();
        byte[] newer = "<newer-user-save/>"u8.ToArray();
        var failure = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignPublicationCommitter.CommitAsync(
            fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt,
            beforeReplacement: () => File.WriteAllBytes(fixture.Intent.DesignPath, newer)));
        Assert.AreEqual("publication_conflict_preserved", failure.Code);
        CollectionAssert.AreEqual(fixture.Intent.CandidateFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(fixture.Intent.PreviousPath));
        var reopened = new DesignRecoveryStore(fixture.RecordPath); var saved = reopened.Read()!;
        await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignPublicationCommitter.CommitAsync(
            reopened, saved.RevisionToken, fixture.SaveReceipt));
        CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(fixture.Intent.PreviousPath));
    }

    [TestMethod]
    public async Task NewDesiredBytesAndStaleRecoveryTokensCannotPublishTheOldCandidate()
    {
        using var fixture = new Fixture();
        var changed = fixture.Store.Save(fixture.Saved.State with { DesiredFileBytes = [0xff] }, fixture.Saved.RevisionToken);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignPublicationCommitter.CommitAsync(fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt))).Code);
        Assert.AreEqual("publication_desired_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignPublicationCommitter.CommitAsync(fixture.Store, changed.RevisionToken, fixture.SaveReceipt))).Code);
        CollectionAssert.AreEqual(fixture.Intent.ExpectedFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        Assert.IsFalse(File.Exists(fixture.Intent.StagedPath));
    }

    [TestMethod]
    public async Task IdenticalExternalSaveConvergesWithoutCreatingAStage()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Intent.DesignPath, fixture.Intent.CandidateFileBytes);
        DateTime timestamp = File.GetLastWriteTimeUtc(fixture.Intent.DesignPath);
        var result = await DesignPublicationCommitter.CommitAsync(fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt);
        Assert.AreEqual(DesignPublicationPhase.Converged, result.Recovery.State.PendingPublication!.Phase);
        Assert.IsFalse(result.ReplacementPerformed); Assert.IsNull(result.PreviousPath);
        Assert.IsFalse(File.Exists(fixture.Intent.StagedPath));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(fixture.Intent.DesignPath));
    }

    [TestMethod]
    public async Task InvalidSaveReceiptAndCancellationLeaveXmlAndRecoveryUnchanged()
    {
        using var fixture = new Fixture();
        var wrong = fixture.SaveReceipt.Clone(); wrong.OperationId = Guid.NewGuid().ToString("D");
        Assert.AreEqual("native_save_not_confirmed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignPublicationCommitter.CommitAsync(fixture.Store, fixture.Saved.RevisionToken, wrong))).Code);
        await Assert.ThrowsAsync<OperationCanceledException>(() => DesignPublicationCommitter.CommitAsync(
            fixture.Store, fixture.Saved.RevisionToken, fixture.SaveReceipt, new CancellationToken(true)));
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        Assert.IsFalse(File.Exists(fixture.Intent.StagedPath));
    }

    // KiCad's first save of a project whose file it has not written yet (or whose sheets were added or renamed since the
    // last save) also writes the sheet list it derives from the schematic, so the saved state digest differs from the
    // one observed before saving. The save-stable digest leaves exactly those derived project-file entries out; with it
    // unchanged the save is the planned one and publication completes with nothing left pending.
    [TestMethod]
    public async Task SaveThatOnlyRewroteTheDerivedProjectEntriesPublishes()
    {
        string stable = new('5', 64);
        using var fixture = new Fixture(stable);
        var receipt = fixture.SaveReceipt.Clone();
        receipt.ObservedState.StateSha256 = new string('e', 64);
        Assert.AreNotEqual(fixture.Saved.State.PendingNativeSave!.ExpectedState.StateSha256, receipt.ObservedState.StateSha256);
        var result = await DesignPublicationCommitter.CommitAsync(fixture.Store, fixture.Saved.RevisionToken, receipt);
        Assert.AreEqual(DesignPublicationPhase.Published, result.Recovery.State.PendingPublication!.Phase);
        Assert.IsTrue(result.ReplacementPerformed);
        CollectionAssert.AreEqual(fixture.Intent.CandidateFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        CollectionAssert.AreEqual(fixture.Intent.ExpectedFileBytes, await File.ReadAllBytesAsync(fixture.Intent.PreviousPath));
    }

    // Only that change is recognized. A save that also changed anything else (the save-stable digest moved), or an
    // observation without the save-stable digest on either side, still needs the full digest unchanged; the XML and the
    // recovery record stay exactly as they were.
    [TestMethod]
    [DataRow("5555555555555555555555555555555555555555555555555555555555555555", "6666666666666666666666666666666666666666666666666666666666666666", DisplayName = "save-stable digest changed")]
    [DataRow("", "5555555555555555555555555555555555555555555555555555555555555555", DisplayName = "no save-stable digest before the save")]
    [DataRow("5555555555555555555555555555555555555555555555555555555555555555", "", DisplayName = "no save-stable digest after the save")]
    [DataRow("", "", DisplayName = "no save-stable digest on either side")]
    [DataRow("5555", "5555", DisplayName = "malformed save-stable digest")]
    public async Task SaveThatChangedMoreThanTheDerivedProjectEntriesIsNotConfirmed(string before, string after)
    {
        using var fixture = new Fixture(before);
        var receipt = fixture.SaveReceipt.Clone();
        receipt.ObservedState.StateSha256 = new string('e', 64);
        receipt.ObservedState.SaveStableStateSha256 = after;
        Assert.AreEqual("native_save_not_confirmed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignPublicationCommitter.CommitAsync(fixture.Store, fixture.Saved.RevisionToken, receipt))).Code);
        Assert.AreEqual(fixture.Saved.RevisionToken, fixture.Store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(fixture.Intent.ExpectedFileBytes, await File.ReadAllBytesAsync(fixture.Intent.DesignPath));
        Assert.IsFalse(File.Exists(fixture.Intent.StagedPath));
    }

    [TestMethod]
    public void VersionSixPreservesExactPathsBytesAndPendingWorkWithoutANativeMutation()
    {
        using var fixture = new Fixture();
        var json = JsonNode.Parse(File.ReadAllBytes(fixture.RecordPath))!;
        Assert.AreEqual(6, json["Version"]!.GetValue<int>());
        var reopened = new DesignRecoveryStore(fixture.RecordPath).Read()!;
        Assert.IsTrue(reopened.State.HasPendingWork); Assert.IsNull(reopened.State.PendingMutation);
        Assert.AreEqual(fixture.Intent.OperationId, reopened.State.PendingPublication!.OperationId);
        CollectionAssert.AreEqual(fixture.Intent.ExpectedFileBytes, reopened.State.PendingPublication.ExpectedFileBytes);
        Assert.AreEqual("pending_recovery_requires_reconciliation", SchematicSynchronizationPlanner.Plan(reopened.State).ErrorCode);
        foreach (var malformed in new[]
        {
            fixture.Intent with { StagedPath = fixture.Intent.DesignPath },
            fixture.Intent with { OperationId = Guid.Empty },
            fixture.Intent with { Phase = (DesignPublicationPhase)99 },
            fixture.Intent with { ExpectedFileBytes = [0xff] }
        })
        {
            Assert.ThrowsExactly<AutomationException>(() => fixture.Store.Save(reopened.State with { PendingPublication = malformed }, reopened.RevisionToken));
            Assert.AreEqual(reopened.RevisionToken, fixture.Store.Read()!.RevisionToken);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("design-publication-recovery-").FullName;
        internal string RecordPath { get; }
        internal DesignRecoveryStore Store { get; }
        internal StoredDesignRecovery Saved { get; }
        internal DesignPublicationIntent Intent { get; }
        internal LifecycleOperationResult SaveReceipt { get; }
        internal Fixture(string saveStableStateSha256 = "")
        {
            RecordPath = Path.Combine(directory, "recovery.json"); Store = new(RecordPath);
            var input = SchematicSynchronizationPlanTests.Fixture();
            var schematic = input.Baseline.Schematic.Clone(); schematic.Document.Project.Path = directory;
            foreach (var screen in schematic.Instances) screen.Metadata.Document.Project.Path = directory;
            var baseline = input.Baseline with { Schematic = schematic };
            var electrical = input.ObservedElectrical!.Clone(); electrical.Hierarchy.Data = schematic.Clone();
            electrical.Hierarchy.Revision.Epoch = Guid.NewGuid().ToString("D");
            var guard = CheckedSchematicToolTests.Request(directory, Guid.NewGuid().ToString("D")).ExpectedState;
            guard.Document = schematic.Document.Clone(); guard.Revision = electrical.Hierarchy.Revision.Clone();
            guard.SaveStableStateSha256 = saveStableStateSha256;
            byte[] before = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, input.KnowledgeLibraries));
            var candidate = baseline with { Schematic = schematic.Clone() };
            candidate.Schematic.Instances[0].Metadata.TitleBlock.Title = "Published candidate";
            Intent = DesignPublicationIntent.Create(Path.Combine(directory, "design.xml"), before,
                Encoding.UTF8.GetBytes(SchematicDesignXml.Write(candidate, input.KnowledgeLibraries)));
            File.WriteAllBytes(Intent.DesignPath, before);
            var save = new CheckedSaveDocument { Document = guard.Document.Clone(), ExpectedState = guard.Clone(), OperationId = Guid.NewGuid().ToString("D") };
            var state = input with { Baseline = baseline, DesiredFileBytes = before, Observed = schematic.Clone(),
                NativeRevision = new(guard.Revision.Epoch, guard.Revision.Sequence),
                BaselineElectrical = electrical.Clone(), ObservedElectrical = electrical.Clone(),
                PendingNativeState = guard, PendingNativeSave = save, PendingPublication = Intent };
            Saved = Store.Save(state, null);
            SaveReceipt = new() { Document = guard.Document.Clone(), OperationId = save.OperationId,
                ProcessEpoch = guard.ProcessEpoch, ObservedState = guard.Clone(), Status = LifecycleOperationStatus.LosSaved };
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
