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

    // Isolated store rule (ledgers p0aa59a1dfc8701ea, p6728215278183167): the one save that leaves a pending operation without
    // its completion clears exactly the operation its receipt names, at the receipt's revision, or replaces it with the
    // keep-and-replan continuation, and moves only the observation. Its receipt names the record revision it produced and
    // replaces a receipt an earlier attempt left when its record replacement failed. The native journey behind
    // NativeSessionTests.DeletedNativeSheetsRebuildFromXmlWithoutLoss proves undo, discard and keep through the public tool
    // against a real KiCad; a journey can neither hand the store the altered next states refused here nor make a record
    // replacement fail after its receipt was written, so they are checked alone (no existing test covers this store rule).
    // The same isolated record also checks how receipts are found (review of 03566919ff, findings 5 and 6): the publication
    // of a kept result is found from its continuation's index file without reading any other receipt, a missing receipt of a
    // kept operation fails closed instead of turning its publication into an ordinary one, a receipt an unsaved attempt left
    // is not reported as a resolution once its operation finished another way (and is called unsaved only when the
    // completion carries the receipt's own native edit), and the publication of a result kept twice names the operation whose
    // result KiCad kept. None of these states can be made in a journey: the journey keeps a result once per stuck operation.
    [TestMethod]
    public async Task OnlyAResolutionOfExactlyThePendingOperationClearsIt()
    {
        using var fixture = new Fixture();
        var saved = fixture.Saved; var state = saved.State;
        var moved = state.ObservedElectrical!.Clone(); moved.Hierarchy.Revision.Sequence += 3;
        var cleared = state with
        {
            PendingMutation = null, PendingNativeState = null, PendingNativeSave = null, PendingPublication = null,
            NativeRevision = new(moved.Hierarchy.Revision.Epoch, moved.Hierarchy.Revision.Sequence), ObservedElectrical = moved
        };
        DesignPendingResolution Receipt(Guid operation, string from, Guid? continuation = null) => new(DesignPendingResolution.CurrentSchemaVersion,
            operation, null, state.InstanceId, continuation is null ? "discard" : "keep-and-replan",
            continuation is null ? DesignPendingResolution.Discarded : DesignPendingResolution.KeepPending, from, "publication", "ordinary", "none",
            state.PendingNativeState!.ProcessEpoch, state.NativeRevision, cleared.NativeRevision, null, 0, continuation, false,
            new string('0', 64), new string('0', 64), null, Google.Protobuf.MessageExtensions.ToByteArray(state.PendingNativeState), state.PendingPublication, null, null, DateTimeOffset.UtcNow);
        string Refused(DesignRecoveryState next, DesignPendingResolution receipt) =>
            Assert.ThrowsExactly<AutomationException>(() => fixture.Store.ResolvePendingOperation(saved, next, receipt)).Code;
        var operation = fixture.Intent.OperationId;

        Assert.AreEqual("pending_operation_mismatch", Refused(cleared, Receipt(Guid.NewGuid(), saved.RevisionToken)));
        Assert.AreEqual("pending_operation_mismatch", Refused(cleared, Receipt(operation, new string('f', 64))));
        var otherBaseline = state.Baseline with { Schematic = state.Baseline.Schematic.Clone() };
        otherBaseline.Schematic.Instances[0].Metadata.TitleBlock.Title = "Rewritten baseline";
        Assert.AreEqual("pending_resolution_changed", Refused(cleared with { Baseline = otherBaseline,
            BaselineElectrical = WithData(state.BaselineElectrical!, otherBaseline.Schematic) }, Receipt(operation, saved.RevisionToken)));
        Assert.AreEqual("pending_resolution_changed", Refused(cleared with { DesiredFileBytes = fixture.Intent.CandidateFileBytes },
            Receipt(operation, saved.RevisionToken)), "The desired XML never changes here.");

        // The keep-and-replan continuation: one prepared publication from the XML the record holds, guarded by exactly the
        // observed native state, under the continuation named by the receipt.
        var guard = state.PendingNativeState!.Clone(); guard.Revision = moved.Hierarchy.Revision.Clone();
        var continuation = Guid.NewGuid();
        var kept = cleared with
        {
            PendingNativeState = guard, PendingPublication = DesignPublicationIntent.Create(fixture.Intent.DesignPath, state.DesiredFileBytes,
                fixture.Intent.CandidateFileBytes, continuation, saved.RevisionToken)
        };
        Assert.AreEqual("pending_resolution_changed", Refused(kept, Receipt(operation, saved.RevisionToken, Guid.NewGuid())));
        Assert.AreEqual("pending_resolution_changed", Refused(kept with { PendingPublication = kept.PendingPublication! with
            { ExpectedFileBytes = fixture.Intent.CandidateFileBytes } }, Receipt(operation, saved.RevisionToken, continuation)));
        Assert.AreEqual("pending_resolution_changed", Refused(kept, Receipt(operation, saved.RevisionToken)), "A discard journals nothing.");
        Assert.AreEqual(saved.RevisionToken, fixture.Store.Read()!.RevisionToken, "Every refusal changed nothing.");
        Assert.IsFalse(Directory.Exists(DesignPendingResolutions.Directory(fixture.RecordPath)), "A refused resolution keeps no receipt.");

        // An earlier attempt whose record replacement failed left its receipt (a discard); the resolution that is saved replaces it.
        DesignPendingResolutions.Write(fixture.RecordPath, Receipt(operation, saved.RevisionToken));
        Assert.AreEqual("discard", DesignPendingResolutions.Find(fixture.RecordPath, operation, state.InstanceId)!.Value.Receipt.Choice);
        var resolved = fixture.Store.ResolvePendingOperation(saved, kept, Receipt(operation, saved.RevisionToken, continuation));
        Assert.AreEqual(continuation, resolved.State.PendingPublication!.OperationId);
        Assert.IsNull(resolved.State.PendingMutation);
        Assert.AreEqual(cleared.NativeRevision, resolved.State.NativeRevision);
        Assert.AreEqual(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries), SchematicDesignXml.Write(resolved.State.Baseline, resolved.State.KnowledgeLibraries));
        CollectionAssert.AreEqual(state.DesiredFileBytes, resolved.State.DesiredFileBytes);
        var receipt = DesignPendingResolutions.Find(fixture.RecordPath, operation, state.InstanceId)!.Value.Receipt;
        Assert.AreEqual(saved.RevisionToken, receipt.ResolvedFromRevisionToken);
        Assert.AreEqual(continuation, receipt.ContinuationOperationId);
        Assert.AreEqual("keep-and-replan", receipt.Choice, "The earlier attempt's receipt was replaced.");
        Assert.AreEqual(resolved.RevisionToken, receipt.ResultRevisionToken, "The receipt names the record revision the resolution produced.");
        Assert.AreEqual(operation, DesignPendingResolutions.KeptBy(fixture.RecordPath, continuation, state.InstanceId)!.Value.Receipt.OperationId,
            "The continuation is found as the publication of the kept result.");
        Assert.AreEqual("design_recovery_changed", Refused(cleared, Receipt(operation, saved.RevisionToken)), "A resolution happens once.");

        // Only the receipt the continuation's index names is read: a damaged receipt of another operation does not block
        // finding it, and an index an unsaved attempt left (its receipt names no such continuation) is ignored.
        string folder = DesignPendingResolutions.Directory(fixture.RecordPath);
        Assert.IsTrue(File.Exists(Path.Combine(folder, continuation.ToString("N") + ".continuation-of." + operation.ToString("N"))));
        string damaged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(damaged, "{ \"SchemaVersion\": ");
        var discardedElsewhere = Guid.NewGuid();
        DesignPendingResolutions.Write(fixture.RecordPath, Receipt(discardedElsewhere, saved.RevisionToken));
        File.WriteAllBytes(Path.Combine(folder, continuation.ToString("N") + ".continuation-of." + discardedElsewhere.ToString("N")), []);
        File.WriteAllBytes(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".continuation-of." + operation.ToString("N")), []);
        Assert.AreEqual(operation, DesignPendingResolutions.KeptBy(fixture.RecordPath, continuation, state.InstanceId)!.Value.Receipt.OperationId);
        Assert.IsNull(DesignPendingResolutions.KeptBy(fixture.RecordPath, Guid.NewGuid(), state.InstanceId), "No index names this operation.");
        var unreadable = Assert.ThrowsExactly<AutomationException>(() => DesignPendingResolutions.Read(damaged));
        Assert.AreEqual("invalid_resolution_receipt", unreadable.Code);
        StringAssert.Contains(unreadable.Message, "move it out of " + folder, "A damaged receipt names what works.");
        StringAssert.Contains(unreadable.Message, "Do not move the receipt of an operation whose kept result is still being published",
            "The advice never removes the check that guards a kept result's discard.");

        // Must-catch (review of fc3f93f070, finding 3): the kept operation's receipt moved out by hand. Its index still names it,
        // so the continuation is refused as a publication whose kept result cannot be checked, never treated as an ordinary
        // publication whose discard needs no check; nothing is read from KiCad and nothing changes. Apply still completes it.
        string keptReceipt = Path.Combine(folder, operation.ToString("N") + ".json"), movedOut = Path.Combine(fixture.RecordPath + ".moved-receipt.json");
        File.Move(keptReceipt, movedOut);
        var missing = Assert.ThrowsExactly<AutomationException>(() => DesignPendingResolutions.KeptBy(fixture.RecordPath, continuation, state.InstanceId));
        Assert.AreEqual("invalid_resolution_receipt", missing.Code, missing.Message);
        StringAssert.Contains(missing.Message, keptReceipt + " is missing");
        StringAssert.Contains(missing.Message, $"kicad_design_sync_apply (operationId {continuation:D})");
        var guarded = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignRecoveryPendingResolution.ResolveAsync(fixture.Store, null,
            state.InstanceId.ToString("D"), resolved.RevisionToken, continuation, PendingOperationChoice.Discard));
        Assert.AreEqual("invalid_resolution_receipt", guarded.Code, "Refused before KiCad is contacted: " + guarded.Message);
        Assert.AreEqual(resolved.RevisionToken, fixture.Store.Read()!.RevisionToken, "Nothing was changed.");
        File.Move(movedOut, keptReceipt);
        Assert.AreEqual(operation, DesignPendingResolutions.KeptBy(fixture.RecordPath, continuation, state.InstanceId)!.Value.Receipt.OperationId);

        // A receipt for an operation that an apply completed: a repeated call naming it is refused, not answered with a
        // resolution. The receipt is called unsaved only when the apply carries the receipt's own native edit (native edit IDs
        // are new for every synchronization); otherwise the operation ID may have been used again after a saved resolution
        // (review of fc3f93f070, finding 4), and the refusal names both rather than saying something false.
        const string Unsaved = "was left by an attempt that was never saved and describes nothing that happened";
        const string Either = "was either left by an attempt that was never saved, or saved before the operation ID was used again";
        async Task<string> Completed(string? receiptEdit, string? appliedEdit)
        {
            var finished = Guid.NewGuid();
            DesignPendingResolutions.Write(fixture.RecordPath, Receipt(finished, resolved.RevisionToken) with { NativeOperationId = receiptEdit });
            var native = appliedEdit is null ? null : new CheckedSchematicBatchReceipt { ProcessEpoch = state.PendingNativeState!.ProcessEpoch,
                OperationId = appliedEdit, Status = CheckedSchematicBatchStatus.CsbsCompleted, ExpectedRequestVerified = true,
                Result = new() { Revision = new Protocol.DocumentRevision { Epoch = resolved.State.NativeRevision.Epoch,
                    Sequence = resolved.State.NativeRevision.Sequence } } };
            new DesignSynchronizationReceipts(fixture.RecordPath).Archive(new DesignSynchronizationReceipt(2, finished, state.InstanceId,
                fixture.Intent.DesignPath, resolved.RevisionToken, new string('a', 64), state.PendingNativeState!.ProcessEpoch,
                resolved.State.NativeRevision.Epoch, resolved.State.NativeRevision.Sequence, native is not null, false,
                native is null ? null : Google.Protobuf.MessageExtensions.ToByteArray(native), null));
            var completed = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignRecoveryPendingResolution.ResolveAsync(fixture.Store, null,
                state.InstanceId.ToString("D"), resolved.RevisionToken, finished, PendingOperationChoice.Discard));
            Assert.AreEqual("pending_operation_completed", completed.Code, completed.Message);
            StringAssert.Contains(completed.Message, $"Operation {finished:D} is no longer pending: it was completed by kicad_design_sync_apply");
            Assert.AreEqual(resolved.RevisionToken, fixture.Store.Read()!.RevisionToken, "Nothing was changed.");
            return completed.Message;
        }
        string edit = Guid.NewGuid().ToString("D");
        string sameEdit = await Completed(edit, edit);
        StringAssert.Contains(sameEdit, Unsaved, "The apply finished the receipt's own native edit, so the resolution was never saved.");
        StringAssert.Contains(sameEdit, "with its native edit " + edit);
        foreach (var (receiptEdit, appliedEdit, what) in new (string?, string?, string)[]
        {
            (edit, Guid.NewGuid().ToString("D"), "the apply sent another native edit (the ID was used again)"),
            (null, null, "neither sent a native edit"), (edit, null, "the apply sent no native edit"), (null, edit, "the receipt names no native edit")
        })
        {
            string message = await Completed(receiptEdit, appliedEdit);
            StringAssert.Contains(message, Either, what);
            StringAssert.Contains(message, "('discard', outcome discarded, from recovery revision " + resolved.RevisionToken + ")", what);
            Assert.IsFalse(message.Contains(Unsaved, StringComparison.Ordinal), what + ": nothing proves the resolution was never saved.");
        }

        // The same for an operation released with kicad_design_recovery_release_exited. A release older than the receipt
        // finished nothing after it (the operation was resumed under its ID and then resolved here): a repeated call reports
        // that resolution.
        async Task<AutomationException?> Released(string? receiptEdit, string? releasedEdit, TimeSpan releasedAfterReceipt)
        {
            var releasedId = Guid.NewGuid();
            var resolution = Receipt(releasedId, resolved.RevisionToken) with { NativeOperationId = receiptEdit };
            DesignReleasedOperations.Write(fixture.RecordPath, DesignReleasedOperation.Create(saved, new InstanceExit(state.InstanceId.ToString("D"),
                state.PendingNativeState!.ProcessEpoch, 4242, null, 9, InstanceExit.ExitStatusEvidence, DateTimeOffset.UtcNow))
                with { OperationId = releasedId, NativeOperationId = releasedEdit, ReleasedAt = resolution.ResolvedAt + releasedAfterReceipt });
            DesignPendingResolutions.Write(fixture.RecordPath, resolution);
            try
            {
                var reported = await DesignRecoveryPendingResolution.ResolveAsync(fixture.Store, null, state.InstanceId.ToString("D"),
                    resolved.RevisionToken, releasedId, PendingOperationChoice.Discard);
                Assert.IsFalse(reported.ResolvedNow);
                Assert.AreEqual(resolution.ResolvedAt, reported.Receipt.ResolvedAt, "The saved resolution is reported.");
                return null;
            }
            catch (AutomationException error)
            {
                Assert.AreEqual("pending_operation_completed", error.Code, error.Message);
                StringAssert.Contains(error.Message, "released with kicad_design_recovery_release_exited");
                return error;
            }
            finally { Assert.AreEqual(resolved.RevisionToken, fixture.Store.Read()!.RevisionToken, "Nothing was changed."); }
        }
        StringAssert.Contains((await Released(edit, edit, TimeSpan.FromMinutes(1)))!.Message, Unsaved, "released with the receipt's own native edit");
        StringAssert.Contains((await Released(edit, Guid.NewGuid().ToString("D"), TimeSpan.FromMinutes(1)))!.Message, Either,
            "released later with another native edit");
        Assert.IsNull(await Released(null, edit, TimeSpan.FromMinutes(-1)), "Released before the resolution: the resolution is reported.");

        // Must-catch (review of fc3f93f070, finding 2a): keep-and-replan chosen again for the continuation. Its own continuation
        // publishes the result KiCad kept for the original operation, so undo is refused naming that operation, not the first
        // continuation, before KiCad is contacted.
        var second = Guid.NewGuid();
        var keptAgain = fixture.Store.ResolvePendingOperation(resolved, resolved.State with
        {
            PendingPublication = DesignPublicationIntent.Create(fixture.Intent.DesignPath, state.DesiredFileBytes, fixture.Intent.CandidateFileBytes,
                second, resolved.RevisionToken)
        }, Receipt(continuation, resolved.RevisionToken, second) with { KeptOperationId = operation });
        Assert.AreEqual(second, keptAgain.State.PendingPublication!.OperationId);
        Assert.AreEqual(continuation, DesignPendingResolutions.KeptBy(fixture.RecordPath, second, state.InstanceId)!.Value.Receipt.OperationId);
        var twice = await Assert.ThrowsExactlyAsync<AutomationException>(() => DesignRecoveryPendingResolution.ResolveAsync(fixture.Store, null,
            state.InstanceId.ToString("D"), keptAgain.RevisionToken, second, PendingOperationChoice.Undo));
        Assert.AreEqual("kept_result_pending", twice.Code, twice.Message);
        StringAssert.Contains(twice.Message, $"Operation {second:D} publishes the result of operation {operation:D} that KiCad kept");
        Assert.IsFalse(twice.Message.Contains(continuation.ToString("D"), StringComparison.Ordinal), "The first continuation is not the kept operation: "
            + twice.Message);
        Assert.AreEqual(keptAgain.RevisionToken, fixture.Store.Read()!.RevisionToken, "Nothing was changed.");

        static SchematicElectricalState WithData(SchematicElectricalState electrical, Kiapi.Schematic.Types.SchematicHierarchyData data)
        {
            var copy = electrical.Clone(); copy.Hierarchy.Data = data.Clone(); return copy;
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
