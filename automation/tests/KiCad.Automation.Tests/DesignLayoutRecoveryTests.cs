using System.Text;
using System.Text.Json.Nodes;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignLayoutRecoveryTests
{
    [TestMethod]
    public void RecoveryToolReportsUnresolvedLayoutWithoutPublishingOrEditingIt()
    {
        using var f = new Fixture(); byte[] before = File.ReadAllBytes(f.Path);
        var tools = new KiCad.Automation.Mcp.RecoveryTools();
        var result = tools.Plan(f.Saved.State.InstanceId.ToString("D"), f.Path, CancellationToken.None);
        Assert.IsFalse(result.IsError ?? false);
        var data = System.Text.Json.JsonSerializer.SerializeToElement(result.StructuredContent);
        var layout = data.GetProperty("pendingLayout");
        Assert.AreEqual(f.Intent.OperationId, layout.GetProperty("operationId").GetGuid());
        Assert.IsFalse(layout.GetProperty("geometryResolved").GetBoolean());
        Assert.IsFalse(layout.GetProperty("liveFilesVerified").GetBoolean());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, data.GetProperty("pendingPublication").ValueKind);
        Assert.IsFalse(data.GetProperty("liveMutationAuthorized").GetBoolean());
        Assert.IsTrue(tools.Plan(Guid.NewGuid().ToString("D"), f.Path, CancellationToken.None).IsError ?? false);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => tools.Plan(f.Saved.State.InstanceId.ToString("D"), f.Path, cancel.Token));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
    }

    [TestMethod]
    [DataRow(false, false), DataRow(true, false), DataRow(false, true)]
    public void RequestedLayoutReopensWithoutPretendingItsWireGeometryIsFinal(bool transform, bool lockOnly)
    {
        using var f = new Fixture(transform, lockOnly);
        byte[] before = File.ReadAllBytes(f.Path);
        Assert.AreEqual(8, JsonNode.Parse(before)!["Version"]!.GetValue<int>());
        Assert.IsNull(JsonNode.Parse(before)!["PendingLayout"]!["Lane"], "A connected move records no synchronization lane.");
        var restored = new DesignRecoveryStore(f.Path).Read()!;
        Assert.IsTrue(restored.State.HasPendingWork);
        Assert.IsNull(restored.State.PendingPublication); Assert.IsNull(restored.State.PendingNativeSave);
        Assert.AreEqual(f.Saved.State.PendingMutation, restored.State.PendingMutation);
        Assert.AreEqual(f.Saved.State.PendingNativeState, restored.State.PendingNativeState);
        CollectionAssert.AreEqual(f.Intent.ExpectedFileBytes, restored.State.PendingLayout!.ExpectedFileBytes);
        CollectionAssert.AreEqual(f.Intent.PlannedDesignFileBytes, restored.State.PendingLayout.PlannedDesignFileBytes);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Save(restored.State, restored.RevisionToken).RevisionToken);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
        var newer = JsonNode.Parse(before)!; newer["Version"] = 9;
        File.WriteAllText(f.Path, newer.ToJsonString());
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Read());
        Assert.AreEqual(newer.ToJsonString(), File.ReadAllText(f.Path));
    }

    [TestMethod]
    public void ActiveLayoutCannotChangeRequestOrDiscardItsPendingNativeOperation()
    {
        using var f = new Fixture(); byte[] before = File.ReadAllBytes(f.Path);
        foreach (var changed in new[]
        {
            f.Intent with { OperationId = Guid.NewGuid() },
            f.Intent with { DesignPath = f.Intent.DesignPath + ".other.xml" },
            f.Intent with { ExpectedFileBytes = f.Intent.PlannedDesignFileBytes },
            f.Intent with { PlannedDesignFileBytes = f.Intent.ExpectedFileBytes },
            f.Intent with { RequestedRecoveryRevisionToken = new string('b', 64) }
        })
        {
            Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(f.Saved.State with { PendingLayout = changed }, f.Saved.RevisionToken));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
        }
        foreach (var changed in new[]
        {
            f.Saved.State with { PendingLayout = null },
            f.Saved.State with { PendingMutation = null },
            f.Saved.State with { PendingNativeState = null },
            f.Saved.State with { PendingNativeSave = f.Publication.Saved.State.PendingNativeSave },
            f.Saved.State with { PendingPublication = f.Publication.Intent }
        })
        {
            Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(changed, f.Saved.RevisionToken));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
        }
        var mutation = f.Saved.State.PendingMutation!.Clone(); mutation.OperationId = Guid.NewGuid().ToString("D");
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(f.Saved.State with { PendingMutation = mutation }, f.Saved.RevisionToken));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
    }

    [TestMethod]
    public void ResolveOnceIntoPublicationRetainsOriginalIdentityAndEngineeringIntent()
    {
        using var f = new Fixture();
        var state = f.Saved.State;
        var planned = SchematicDesignXml.Read(Encoding.UTF8.GetString(f.Intent.PlannedDesignFileBytes), state.KnowledgeLibraries);
        var different = planned with { Engineering = planned.Engineering with { Circuit = planned.Engineering.Circuit with
            { Components = planned.Engineering.Circuit.Components.Select((c, i) => c with { Reference = "U" + (700 + i) }).ToArray() } } };
        var publication = DesignPublicationIntent.Create(f.Intent.DesignPath, f.Intent.ExpectedFileBytes,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(different, state.KnowledgeLibraries)), f.Intent.OperationId,
            f.Intent.RequestedRecoveryRevisionToken);
        byte[] before = File.ReadAllBytes(f.Path);
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(state with { PendingLayout = null, PendingPublication = publication }, f.Saved.RevisionToken));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
        publication = publication with { CandidateFileBytes = f.Intent.PlannedDesignFileBytes };
        var resolved = f.Store.Save(state with { PendingLayout = null, PendingPublication = publication }, f.Saved.RevisionToken);
        Assert.IsNull(resolved.State.PendingLayout);
        Assert.AreEqual(7, JsonNode.Parse(File.ReadAllBytes(f.Path))!["Version"]!.GetValue<int>());
        Assert.AreEqual(f.Intent.OperationId, resolved.State.PendingPublication!.OperationId);
        Assert.AreEqual(f.Intent.RequestedRecoveryRevisionToken, resolved.State.PendingPublication.RequestedRecoveryRevisionToken);
        Assert.AreEqual(state.PendingMutation, resolved.State.PendingMutation);
        Assert.AreEqual(state.PendingNativeState, resolved.State.PendingNativeState);
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(resolved.State with
            { PendingPublication = publication with { CandidateFileBytes = f.Intent.ExpectedFileBytes } }, resolved.RevisionToken));
        Assert.AreEqual(resolved.RevisionToken, f.Store.Read()!.RevisionToken);
    }

    [TestMethod]
    public void LayoutNeedsAConnectedMoveAndCannotAliasRecoveryOrAcceptAStaleWriter()
    {
        using var f = new Fixture(); byte[] before = File.ReadAllBytes(f.Path);
        var mutation = f.Saved.State.PendingMutation!.Clone(); mutation.Operations.Clear();
        mutation.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = "No connected movement" } });
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(f.Saved.State with { PendingMutation = mutation }, f.Saved.RevisionToken));
        var pathCollision = new DesignRecoveryStore(f.Intent.DesignPath);
        byte[] xml = File.ReadAllBytes(f.Intent.DesignPath);
        Assert.ThrowsExactly<AutomationException>(() => pathCollision.Save(f.Saved.State, null));
        CollectionAssert.AreEqual(xml, File.ReadAllBytes(f.Intent.DesignPath));
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(f.Saved.State, new string('c', 64)));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Path));
    }

    // CN-1 §9.3/§9.4: a lane realization journals its planning lane at envelope version 10
    // and contains only creations, updates and library-cache replacements followed by one
    // whole-batch connectivity assertion (mandatory for connection realizations).
    private static ApplySchematicItemBatch LaneMutation(Fixture f, params SchematicItemOperation[] operations)
    {
        var mutation = f.Saved.State.PendingMutation!.Clone();
        mutation.Operations.Clear(); mutation.Operations.Add(operations);
        return mutation;
    }

    private static SchematicItemOperation Created() =>
        new() { Create = Google.Protobuf.WellKnownTypes.Any.Pack(new Google.Protobuf.WellKnownTypes.Empty()) };

    private static SchematicItemOperation Assertion() => new() { AssertConnectivity = new SchematicConnectivityAssertion() };

    [TestMethod]
    [DataRow(DesignLayoutIntent.ConnectionRealizationLane), DataRow(DesignLayoutIntent.RebuildLane)]
    public void LaneRealizationJournalsItsPlanningLaneAtVersionTen(string lane)
    {
        using var f = new Fixture();
        var intent = f.Intent with { Lane = lane };
        string path = f.Path + ".lane.json"; var store = new DesignRecoveryStore(path);
        var saved = store.Save(f.Saved.State with { PendingMutation = LaneMutation(f, Created(), Assertion()), PendingLayout = intent }, null);
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        Assert.AreEqual(10, json["Version"]!.GetValue<int>());
        Assert.AreEqual(lane, json["PendingLayout"]!["Lane"]!.GetValue<string>());
        var restored = new DesignRecoveryStore(path).Read()!;
        Assert.AreEqual(lane, restored.State.PendingLayout!.Lane);
        Assert.AreEqual(saved.State.PendingMutation, restored.State.PendingMutation);
        var older = json.DeepClone(); older["Version"] = 8;
        File.WriteAllText(path, older.ToJsonString());
        Assert.AreEqual("invalid_design_recovery", Assert.ThrowsExactly<AutomationException>(() => store.Read()).Code,
            "A lane record must not load under an older envelope version.");
    }

    [TestMethod]
    public void LaneRealizationRejectsBatchesOutsideItsContract()
    {
        using var f = new Fixture();
        var connection = f.Intent with { Lane = DesignLayoutIntent.ConnectionRealizationLane };
        var move = f.Saved.State.PendingMutation!.Operations[0].Clone();
        foreach (var (layout, operations) in new (DesignLayoutIntent, SchematicItemOperation[])[]
        {
            (connection, [Created()]),                       // connection realization without its assertion
            (connection, [Assertion(), Created()]),          // assertion not last
            (connection, [Created(), Assertion(), Assertion()]),
            (connection, [move, Assertion()]),               // connected moves belong to the move path
            (f.Intent with { Lane = DesignLayoutIntent.RebuildLane }, [Created(), new() { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } }]),
            (f.Intent with { Lane = "unknown-lane" }, [Created(), Assertion()]),
        })
        {
            var failure = Assert.ThrowsExactly<AutomationException>(() => new DesignRecoveryStore(f.Path + ".lane.json").Save(
                f.Saved.State with { PendingMutation = LaneMutation(f, operations), PendingLayout = layout }, null));
            Assert.AreEqual("invalid_layout_intent", failure.Code);
        }
        // A rebuild may omit the assertion; an ordinary connected move still needs its move.
        new DesignRecoveryStore(f.Path + ".rebuild.json").Save(f.Saved.State with { PendingMutation = LaneMutation(f, Created()),
            PendingLayout = f.Intent with { Lane = DesignLayoutIntent.RebuildLane } }, null);
        Assert.ThrowsExactly<ArgumentException>(() => DesignLayoutIntent.Create(f.Intent.DesignPath, f.Intent.ExpectedFileBytes,
            f.Intent.PlannedDesignFileBytes, Guid.NewGuid(), f.Intent.RequestedRecoveryRevisionToken, "unknown-lane"));
    }

    [TestMethod]
    public void RejectedRealizationIsAbandonedOnlyWhenNothingNativeChanged()
    {
        using var f = new Fixture();
        var store = new DesignRecoveryStore(f.Path + ".lane.json");
        var lane = store.Save(f.Saved.State with { PendingMutation = LaneMutation(f, Created(), Assertion()),
            PendingLayout = f.Intent with { Lane = DesignLayoutIntent.ConnectionRealizationLane } }, null);
        var native = lane.State.PendingNativeState!;
        CheckedSchematicBatchReceipt Receipt(CheckedSchematicBatchStatus status, DocumentLifecycleState after, string? operationId = null) => new()
        {
            Document = lane.State.PendingMutation!.Document.Clone(), ProcessEpoch = native.ProcessEpoch,
            OperationId = operationId ?? lane.State.PendingMutation.OperationId, Status = status, ExpectedRequestVerified = true,
            ObservedBefore = native.Clone(), ObservedAfter = after.Clone(), ErrorCode = "realization_connectivity_mismatch"
        };
        var changed = native.Clone(); changed.Revision.Sequence++;
        foreach (var receipt in new[] { Receipt(CheckedSchematicBatchStatus.CsbsCompleted, native),
            Receipt(CheckedSchematicBatchStatus.CsbsRejected, changed),
            Receipt(CheckedSchematicBatchStatus.CsbsRejected, native, Guid.NewGuid().ToString("D")) })
        {
            var failure = Assert.ThrowsExactly<AutomationException>(() => store.AbandonRejectedRealization(lane, receipt));
            Assert.AreEqual("invalid_layout_intent", failure.Code, receipt.Status + ": " + failure.Code + " " + failure.Message);
        }
        Assert.AreEqual("invalid_layout_intent", Assert.ThrowsExactly<AutomationException>(
            () => f.Store.AbandonRejectedRealization(f.Saved, Receipt(CheckedSchematicBatchStatus.CsbsRejected, native))).Code,
            "A connected move is not a lane realization.");
        var abandoned = store.AbandonRejectedRealization(lane, Receipt(CheckedSchematicBatchStatus.CsbsRejected, native));
        Assert.IsNull(abandoned.State.PendingMutation); Assert.IsNull(abandoned.State.PendingNativeState);
        Assert.IsNull(abandoned.State.PendingLayout);
        CollectionAssert.AreEqual(lane.State.DesiredFileBytes, abandoned.State.DesiredFileBytes);
        Assert.AreEqual(SchematicDesignXml.Write(lane.State.Baseline, lane.State.KnowledgeLibraries),
            SchematicDesignXml.Write(abandoned.State.Baseline, abandoned.State.KnowledgeLibraries));
    }

    private sealed class Fixture : IDisposable
    {
        internal DesignPublicationRecoveryTests.Fixture Publication { get; } = new();
        internal string Path { get; }
        internal DesignRecoveryStore Store { get; }
        internal StoredDesignRecovery Saved { get; }
        internal DesignLayoutIntent Intent { get; }
        internal Fixture(bool transform = false, bool lockOnly = false)
        {
            Path = Publication.RecordPath + ".layout.json"; Store = new(Path);
            var state = Publication.Saved.State;
            var guard = state.PendingNativeState!;
            var mutation = new ApplySchematicItemBatch { Document = guard.Document.Clone(), DocumentEpoch = guard.Revision.Epoch,
                ExpectedRevision = guard.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), OriginId = state.OriginId.ToString("D") };
            var movement = new SchematicConnectedSymbolMove { Delta = new() { XNm = 2540000 } };
            movement.Symbols.Add(new KIID { Value = state.Baseline.SymbolBindings[0].NativeObjectId.ToString("D") });
            mutation.Operations.Add(new SchematicItemOperation { MoveConnectedSymbols = movement });
            if (transform)
            {
                mutation.Operations.Clear();
                var rotation = new SchematicConnectedSymbolTransform { Pivot = new() { XNm = 10000000, YNm = 20000000 },
                    Kind = SchematicConnectedTransformKind.SctRotateCounterclockwise };
                rotation.Symbols.Add(movement.Symbols.Select(s => s.Clone()));
                mutation.Operations.Add(new SchematicItemOperation { TransformConnectedSymbols = rotation });
            }
            if (lockOnly)
            {
                mutation.Operations.Clear();
                var locks = new SchematicSymbolLocks { Locked = Kiapi.Common.Types.LockedState.LsLocked };
                locks.Symbols.Add(movement.Symbols.Select(s => s.Clone()));
                mutation.Operations.Add(new SchematicItemOperation { SetSymbolLocks = locks });
            }
            Intent = DesignLayoutIntent.Create(Publication.Intent.DesignPath, Publication.Intent.ExpectedFileBytes,
                Publication.Intent.CandidateFileBytes, Guid.NewGuid(), Publication.Saved.RevisionToken);
            Saved = Store.Save(state with { PendingMutation = mutation, PendingPublication = null, PendingNativeSave = null,
                PendingLayout = Intent }, null);
        }
        public void Dispose() => Publication.Dispose();
    }
}
