using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignRecoveryStoreTests
{
    [TestMethod]
    public void ComputedNetChainMembershipIsTheOnlyDerivedPublicationDifference()
    {
        var source = Fixture();
        var before = source.Baseline with { Schematic = source.Baseline.Schematic.Clone() };
        foreach (var screen in before.Schematic.Instances)
            screen.Metadata.NetChains.Add(new SchematicNetChainDefinition
            {
                Name = "PATH", From = new() { Reference = "U1", Pin = "1" },
                To = new() { Reference = "U2", Pin = "1" }, MemberNets = { "/A" }
            });
        var after = before with { Schematic = before.Schematic.Clone() };
        foreach (var screen in after.Schematic.Instances)
        {
            screen.Metadata.NetChains[0].Committed = true;
            screen.Metadata.NetChains[0].Exclusions = new();
        }
        byte[] beforeBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(before, source.KnowledgeLibraries));
        byte[] afterBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(after, source.KnowledgeLibraries));
        Assert.IsTrue(DesignRecoveryStore.ComputedNetChainOnlyChange(beforeBytes, afterBytes, source.KnowledgeLibraries));
        after.Schematic.Instances[0].Metadata.NetChains[0].NetClass = "changed";
        afterBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(after, source.KnowledgeLibraries));
        Assert.IsFalse(DesignRecoveryStore.ComputedNetChainOnlyChange(beforeBytes, afterBytes, source.KnowledgeLibraries));
    }

    internal static DesignRecoveryState Fixture()
    {
        var (design, library) = SchematicModelProjectionTests.Fixture();
        return new(Guid.NewGuid(), Guid.NewGuid(), new("native-epoch", 42), false, design,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [library])), design.Schematic.Clone(), [library]);
    }

    private static void Isolated(Action<string> test)
    {
        string directory = Directory.CreateTempSubdirectory("kicad-design-recovery-").FullName;
        try { test(Path.Combine(directory, "recovery.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static ApplySchematicItemBatch Mutation(DesignRecoveryState state)
    {
        var request = new ApplySchematicItemBatch
        {
            Document = state.Observed.Document.Clone(), DocumentEpoch = state.NativeRevision.Epoch,
            ExpectedRevision = new() { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence },
            OriginId = state.OriginId.ToString("D"), OperationId = "stable-operation-42"
        };
        var operation = new SchematicItemOperation { MoveConnectedSymbols = new() { Delta = new() { XNm = 1000000 } } };
        operation.MoveConnectedSymbols.Symbols.Add(new Kiapi.Common.Types.KIID { Value = state.Baseline.SymbolBindings[0].NativeObjectId.ToString("D") });
        request.Operations.Add(operation);
        return request;
    }

    [TestMethod]
    public void ReopenPreservesHierarchyRequirementsLibrariesAndInvalidDesiredBytes() => Isolated(path =>
    {
        var state = Fixture() with { DesiredFileBytes = [0xff, 0x00, 0x3c, 0x78] };
        var store = new DesignRecoveryStore(path);
        Assert.IsNull(store.Read());
        var saved = store.Save(state, null);
        var recovered = new DesignRecoveryStore(path).Read()!;
        Assert.AreEqual(saved.RevisionToken, recovered.RevisionToken);
        Assert.AreEqual(state.OriginId, recovered.State.OriginId);
        Assert.AreEqual(state.InstanceId, recovered.State.InstanceId);
        Assert.AreEqual(state.NativeRevision, recovered.State.NativeRevision);
        Assert.IsFalse(recovered.State.TrackingComplete);
        Assert.AreEqual(state.Observed, recovered.State.Observed);
        Assert.IsTrue(recovered.State.Observed.Instances.Count > 1);
        CollectionAssert.AreEqual(state.DesiredFileBytes, recovered.State.DesiredFileBytes);
        Assert.AreEqual(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries),
            SchematicDesignXml.Write(recovered.State.Baseline, recovered.State.KnowledgeLibraries));
        CollectionAssert.AreEqual(state.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary).ToArray(),
            recovered.State.KnowledgeLibraries.Select(ComponentKnowledgeXml.WriteLibrary).ToArray());
        var locations = new SchematicFileLocations { Document = state.Observed.Document.Clone(), ProcessEpoch = "fixture-process",
            Revision = new() { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence } };
        foreach (var screen in state.Observed.Instances)
        {
            string nativePath = SchematicNativeSheetChanges.Key(screen);
            string filename = Path.Combine(Path.GetTempPath(), screen.Metadata.ScreenId.Value + ".kicad_sch");
            locations.Locations.Add(new SchematicFileLocation { Path = screen.Metadata.Document.SheetPath.Clone(), ScreenId = screen.Metadata.ScreenId.Clone(),
                LoadedFilename = filename, ResolvedFilename = filename, DeclarationMatchesLoaded = true,
                DeclaredFilename = SchematicNativeSheetChanges.SheetSymbolOf(state.Observed, nativePath)?.FilenameField?.Text?.Text_ ?? filename });
        }
        var withFiles = store.Save(state with { NativeFileLocations = locations }, saved.RevisionToken);
        Assert.AreEqual(locations, new DesignRecoveryStore(path).Read()!.State.NativeFileLocations);
        using (var document = JsonDocument.Parse(File.ReadAllBytes(path))) Assert.AreEqual(11, document.RootElement.GetProperty("Version").GetInt32());
        Assert.AreEqual(withFiles.RevisionToken, store.Save(withFiles.State, withFiles.RevisionToken).RevisionToken);
        var incomplete = locations.Clone(); incomplete.Locations.RemoveAt(0);
        Assert.ThrowsExactly<AutomationException>(() => store.Save(withFiles.State with { NativeFileLocations = incomplete }, withFiles.RevisionToken));
        Assert.AreEqual(withFiles.RevisionToken, store.Read()!.RevisionToken, "A bad file observation cannot replace recovery.");
        var stale = locations.Clone(); stale.Revision.Sequence--;
        Assert.IsNull(NativeSheetFileLocations.Find(withFiles.State with { NativeFileLocations = stale },
            SchematicNativeSheetChanges.Key(state.Observed.Instances[0])), "Stale provenance is never used to resolve a move.");
    });

    [TestMethod]
    public void PendingOperationSurvivesExactlyWithoutClaimingItCommitted() => Isolated(path =>
    {
        var state = Fixture(); state = state with { PendingMutation = Mutation(state) };
        byte[] request = state.PendingMutation.ToByteArray();
        var stored = new DesignRecoveryStore(path).Save(state, null);
        var recovered = new DesignRecoveryStore(path).Read()!;
        CollectionAssert.AreEqual(request, recovered.State.PendingMutation!.ToByteArray());
        Assert.AreEqual(state.NativeRevision, recovered.State.NativeRevision);
        Assert.AreEqual(state.Observed, recovered.State.Observed);
        Assert.IsFalse(recovered.State.TrackingComplete);
        state.PendingMutation.OperationId = "changed-in-memory";
        Assert.AreEqual("stable-operation-42", stored.State.PendingMutation!.OperationId);
    });

    [TestMethod]
    public void UnchangedSaveHasNoChurnAndStaleWriterCannotReplaceNewState() => Isolated(path =>
    {
        var store = new DesignRecoveryStore(path); var state = Fixture();
        var first = store.Save(state, null);
        var writtenAt = File.GetLastWriteTimeUtc(path); byte[] bytes = File.ReadAllBytes(path);
        var repeated = store.Save(state, first.RevisionToken);
        Assert.AreEqual(first.RevisionToken, repeated.RevisionToken);
        Assert.AreEqual(writtenAt, File.GetLastWriteTimeUtc(path));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        var next = store.Save(state with { DesiredFileBytes = Encoding.UTF8.GetBytes("<partial") }, first.RevisionToken);
        Assert.AreNotEqual(first.RevisionToken, next.RevisionToken);
        Assert.AreEqual("design_recovery_changed", Assert.ThrowsExactly<AutomationException>(() =>
            store.Save(state, first.RevisionToken)).Code);
        Assert.AreEqual(next.RevisionToken, store.Read()!.RevisionToken);
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
    });

    [TestMethod]
    public void WrongEpochOriginOrTargetCannotBecomeAnUnconfirmedRetry() => Isolated(path =>
    {
        var state = Fixture(); var store = new DesignRecoveryStore(path); var saved = store.Save(state, null);
        foreach (Action<ApplySchematicItemBatch> change in new Action<ApplySchematicItemBatch>[]
        {
            p => p.DocumentEpoch = "restarted",
            p => p.ExpectedRevision.Sequence++,
            p => p.OriginId = Guid.NewGuid().ToString("D"),
            p => p.OperationId = "",
            p => p.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D"),
            p => p.Operations[0].TargetDocument = new() { SheetPath = new() },
            p => p.Operations.Clear()
        })
        {
            var pending = Mutation(state); change(pending);
            Assert.AreEqual("invalid_design_recovery", Assert.ThrowsExactly<AutomationException>(() =>
                store.Save(state with { PendingMutation = pending }, saved.RevisionToken)).Code);
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        }
    });

    [TestMethod]
    public void ContendingWriterAndBadNativeRootPreserveTheLastRecovery() => Isolated(path =>
    {
        var state = Fixture(); var store = new DesignRecoveryStore(path); var saved = store.Save(state, null);
        using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual("design_recovery_io", Assert.ThrowsExactly<AutomationException>(() =>
                store.Save(state with { DesiredFileBytes = [] }, saved.RevisionToken)).Code);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        var observed = state.Observed.Clone(); observed.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
        Assert.AreEqual("invalid_design_recovery", Assert.ThrowsExactly<AutomationException>(() =>
            store.Save(state with { Observed = observed }, saved.RevisionToken)).Code);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public void HierarchyChoicesSurviveReopenAndNeverOverwriteChangedRecovery() => Isolated(path =>
    {
        var state = Fixture();
        var hierarchy = SchematicHierarchyTopologyTests.Fixture();
        state = state with { Baseline = state.Baseline with { Schematic = hierarchy, SheetBindings = [], SymbolBindings = [] },
            Observed = hierarchy.Clone() };
        var desired = state.Baseline.Schematic.Clone(); var observed = state.Observed.Clone();
        desired.Instances[0].Metadata.TitleBlock = new() { Title = "XML page" };
        observed.Instances[0].Metadata.TitleBlock = new() { Title = "Native page" };
        state = state with { Observed = observed, DesiredFileBytes = Encoding.UTF8.GetBytes(
            SchematicDesignXml.Write(state.Baseline with { Schematic = desired }, state.KnowledgeLibraries)) };
        var store = new DesignRecoveryStore(path); var saved = store.Save(state, null);
        var plan = DesignRecoveryStore.PlanHierarchy(state);
        Assert.IsTrue(plan.Conflicts.Count > 0, plan.ErrorMessage);
        var choices = plan.Conflicts.ToDictionary(c => c.InstancePath, _ => SchematicConflictChoice.Xml);
        string snapshot = DesignRecoveryStore.HierarchySnapshotToken(state);
        var resolved = store.ResolveHierarchy(saved.RevisionToken, snapshot, choices);
        var reopened = new DesignRecoveryStore(path).Read()!;
        Assert.AreEqual(resolved.RevisionToken, reopened.RevisionToken);
        Assert.IsTrue(DesignRecoveryStore.PlanHierarchy(reopened.State).CanApply);
        Assert.AreEqual("XML page", DesignRecoveryStore.PlanHierarchy(reopened.State).Merged!.Instances[0].Metadata.TitleBlock.Title);
        CollectionAssert.AreEqual(saved.State.DesiredFileBytes, reopened.State.DesiredFileBytes);
        Assert.AreEqual(SchematicDesignXml.Write(saved.State.Baseline, state.KnowledgeLibraries),
            SchematicDesignXml.Write(reopened.State.Baseline, state.KnowledgeLibraries));
        Assert.AreEqual(resolved.RevisionToken, store.ResolveHierarchy(resolved.RevisionToken, snapshot, choices).RevisionToken);
        Assert.ThrowsExactly<AutomationException>(() => store.ResolveHierarchy(saved.RevisionToken, snapshot, choices));
        Assert.ThrowsExactly<AutomationException>(() => store.Save(reopened.State with
            { NativeRevision = new(reopened.State.NativeRevision.Epoch, reopened.State.NativeRevision.Sequence + 1) }, reopened.RevisionToken));
        Assert.ThrowsExactly<AutomationException>(() => store.Save(reopened.State with
            { NativeRevision = new("replacement-document", reopened.State.NativeRevision.Sequence) }, reopened.RevisionToken));
        var newerNative = observed.Clone(); newerNative.Instances[0].Metadata.TitleBlock.Title = "Later page";
        Assert.ThrowsExactly<AutomationException>(() => store.Save(reopened.State with { Observed = newerNative }, reopened.RevisionToken));
        Assert.AreEqual(resolved.RevisionToken, store.Read()!.RevisionToken);
        var refreshed = store.Save(reopened.State with { Observed = newerNative, HierarchyResolution = null }, reopened.RevisionToken);
        Assert.ThrowsExactly<AutomationException>(() => store.ResolveHierarchy(refreshed.RevisionToken, snapshot, choices));
        Assert.AreEqual(refreshed.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public void InvalidDesiredBytesCannotBeResolvedOrDiscarded() => Isolated(path =>
    {
        var state = Fixture() with { DesiredFileBytes = [0xff, 0x3c] };
        var store = new DesignRecoveryStore(path); var saved = store.Save(state, null);
        Assert.ThrowsExactly<AutomationException>(() => store.ResolveHierarchy(saved.RevisionToken, "snapshot",
            new Dictionary<string, SchematicConflictChoice>()));
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        CollectionAssert.AreEqual(state.DesiredFileBytes, store.Read()!.State.DesiredFileBytes);
    });

    [TestMethod]
    public void CorruptUnknownAndFutureRecoveryRecordsAreRejectedWithoutReplacement() => Isolated(path =>
    {
        var store = new DesignRecoveryStore(path); var state = Fixture(); store.Save(state, null);
        string original = File.ReadAllText(path);
        foreach (string changed in new[]
        {
            "{broken", original[..^1] + ",\"Version\":1}",
            Change("Version", JsonValue.Create(99)), Change("Unexpected", JsonValue.Create(true)),
            Change("PendingMutation", JsonValue.Create("not-base64")), Change("DesiredFileBytes", null)
        })
        {
            File.WriteAllText(path, changed);
            Assert.ThrowsExactly<AutomationException>(() => store.Read());
            Assert.AreEqual(changed, File.ReadAllText(path));
        }
        string Change(string key, JsonNode? value)
        {
            var root = JsonNode.Parse(original)!; root[key] = value; return root.ToJsonString();
        }
    });
}
