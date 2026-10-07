using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicTypedMutationTests
{
    private static readonly string Id = "12345678-1234-4234-8234-123456789012";

    [TestMethod]
    public void BuildsTypedConnectionItemsWithoutAnyPayloadAuthoring()
    {
        var wire = (SchematicLine)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Wire, Id,
            1_000, 2_000, 4_000, 2_000, null);
        Assert.AreEqual(Id, wire.Id.Value);
        Assert.AreEqual(SchematicLineType.SltWire, wire.Type);
        Assert.AreEqual(1_000, wire.Start.XNm);
        Assert.AreEqual(4_000, wire.End.XNm);

        var bus = (SchematicLine)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Bus, Id,
            1_000, 2_000, 4_000, 2_000, null);
        Assert.AreEqual(SchematicLineType.SltBus, bus.Type);

        var junction = (Junction)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Junction, Id,
            1_000, 2_000, null, null, null);
        Assert.AreEqual(2_000, junction.Position.YNm);

        var label = (LocalLabel)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.LocalLabel, Id,
            1_000, 2_000, null, null, "DATA");
        Assert.AreEqual("DATA", label.Text.Text_);
        Assert.AreEqual(SchematicLabelSpinStyle.SlssRight, label.SpinStyle);

        var note = (SchematicText)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Text, Id,
            1_000, 2_000, null, null, "note");
        Assert.AreEqual("note", note.Text.Text_);
        Assert.AreEqual(1_000, note.Text.Position.XNm);
    }

    [TestMethod]
    public void RejectsMissingEndpointsTextAndNonNativeCoordinates()
    {
        var missing = Assert.ThrowsExactly<AutomationException>(() => SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Wire, Id, 1_000, 2_000, null, null, null));
        Assert.AreEqual("missing_connection_endpoint", missing.Code);

        var badCoordinate = Assert.ThrowsExactly<AutomationException>(() => SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Junction, Id, 1_001, 2_000, null, null, null));
        Assert.AreEqual("invalid_coordinate", badCoordinate.Code);

        var badId = Assert.ThrowsExactly<AutomationException>(() => SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Junction, "not-a-uuid", 1_000, 2_000, null, null, null));
        Assert.AreEqual("invalid_identity", badId.Code);
    }

    [TestMethod]
    public async Task CreateRequiresRootCheckedObservationAndRetainsExactOperationReplay()
    {
        using var fixture = await Fixture.CreateAsync();
        string expected = fixture.ExpectedJson;
        string operation = Guid.NewGuid().ToString("D");
        var created = await fixture.Tools.CreateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            expected, SchematicConnectionItemKind.LocalLabel, Id,
            1_000, 2_000, operation, default, text: "DATA");
        Assert.IsFalse(created.IsError ?? false, Data(created).GetRawText());
        Assert.AreEqual(1, fixture.Batches.Count);
        var create = fixture.Batches.Single().Batch.Operations.Single().Create.Unpack<LocalLabel>();
        Assert.AreEqual(Id, create.Id.Value);
        Assert.AreEqual("DATA", create.Text.Text_);
        Assert.AreEqual(1, fixture.Items.Count);

        var replay = await fixture.Tools.CreateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            expected, SchematicConnectionItemKind.LocalLabel, Id,
            1_000, 2_000, operation, default, text: "DATA");
        Assert.IsFalse(replay.IsError ?? false, Data(replay).GetRawText());
        Assert.AreEqual(1, fixture.Batches.Count, "The native operation receipt must make an exact retry idempotent.");
        Assert.AreEqual(1, fixture.Items.Count);

        var child = fixture.State.State.Document.Clone();
        child.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        AssertError(await fixture.Tools.CreateConnectionItem(fixture.InstanceId,
            SchematicJson.Formatter.Format(child), fixture.ExpectedJson,
            SchematicConnectionItemKind.Wire, Guid.NewGuid().ToString("D"),
            1_000, 2_000, Guid.NewGuid().ToString("D"), default, endXNm: 4_000, endYNm: 2_000),
            "invalid_checked_document");

        AssertError(await fixture.Tools.CreateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            SchematicJson.Formatter.Format(fixture.State.State), SchematicConnectionItemKind.Wire,
            Guid.NewGuid().ToString("D"), 1_000, 2_000, Guid.NewGuid().ToString("D"), default,
            endXNm: 4_000, endYNm: 2_000), "invalid_checked_state");
    }

    [TestMethod]
    public async Task LibraryCacheReplaceUsesCompleteCheckedHierarchyScreenTargetAndReceiptReplay()
    {
        using var fixture = await Fixture.CreateAsync();
        var cache = new SchematicCachedSymbol
        {
            CacheKey = "Fixture:Cached",
            Definition = new() { Id = new() { LibraryNickname = "Fixture", EntryName = "Cached" } }
        };
        string definitions = "[" + SchematicJson.Formatter.Format(cache) + "]";
        string operation = Guid.NewGuid().ToString("D");
        string expected = fixture.ExpectedJson;

        var result = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            expected, fixture.ScreenId, definitions, operation, default);
        Assert.IsFalse(result.IsError ?? false, Data(result).GetRawText());
        Assert.AreEqual(fixture.TargetDocument, fixture.Batches.Single().Batch.Operations.Single().TargetDocument);
        Assert.AreEqual(1, fixture.Inspections, "The tool must inspect the retained receipt before its live checkpoint.");
        Assert.AreEqual(1, fixture.Captures, "A new operation needs one live checked-state confirmation.");
        Assert.IsFalse(Data(result).GetProperty("designFilesSaved").GetBoolean());

        var replay = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            expected, fixture.ScreenId, definitions, operation, default);
        Assert.IsFalse(replay.IsError ?? false, Data(replay).GetRawText());
        Assert.IsTrue(Data(replay).GetProperty("replayed").GetBoolean());
        Assert.IsFalse(Data(replay).GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual(1, fixture.Batches.Count);
        Assert.AreEqual(1, fixture.Captures, "Replay must not capture a now-advanced live state.");

        // Legacy lifecycle-only callers remain root-scoped and keep the original native request
        // shape; they cannot silently bind a child screen.
        var legacy = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            SchematicJson.Formatter.Format(fixture.State.State), fixture.ScreenId, definitions,
            Guid.NewGuid().ToString("D"), default);
        Assert.IsFalse(legacy.IsError ?? false, Data(legacy).GetRawText());
        Assert.IsNull(fixture.Batches[^1].Batch.Operations.Single().TargetDocument);
    }

    [TestMethod]
    public async Task SharedLibraryCacheReplacementRejectsDisagreementAndRetainsUntrustedReplyIdentity()
    {
        using var fixture = await Fixture.CreateAsync();
        var shared = fixture.State.Electrical.Hierarchy.Data.Instances[0].Clone();
        shared.Metadata.Document.SheetPath.Path[^1].Value = Guid.NewGuid().ToString("D");
        fixture.State.Electrical.Hierarchy.Data.Instances.Add(shared);
        shared.CachedSymbols.Add(new SchematicCachedSymbol
        {
            CacheKey = "Fixture:Different",
            Definition = new() { Id = new() { LibraryNickname = "Fixture", EntryName = "Different" } }
        });
        var refused = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            fixture.ExpectedJson, fixture.ScreenId, "[]", Guid.NewGuid().ToString("D"), default);
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual("inconsistent_shared_screen_cache", Data(refused).GetProperty("errorCode").GetString());
        Assert.AreEqual(0, fixture.Batches.Count);

        shared.CachedSymbols.Clear();
        fixture.TamperCacheReceipt = true;
        string operation = Guid.NewGuid().ToString("D");
        var untrusted = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            fixture.ExpectedJson, fixture.ScreenId, "[]", operation, default);
        Assert.IsTrue(untrusted.IsError);
        Assert.AreEqual(operation, Data(untrusted).GetProperty("operationId").GetString());
        Assert.IsTrue(Data(untrusted).GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual("not_confirmed", Data(untrusted).GetProperty("outcome").GetString());
        Assert.AreEqual("invalid_checked_batch", Data(untrusted).GetProperty("errorCode").GetString());
        Assert.AreEqual(1, fixture.Batches.Count);
    }

    [TestMethod]
    public async Task LibraryCacheLostReplyRetainsOperationAndReplaysWithoutAnotherMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        string expected = fixture.ExpectedJson;
        string operation = Guid.NewGuid().ToString("D");
        fixture.CacheTransportFailure = new NngException(5, "Lost fixture reply", requestDelivered: true);
        var lost = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            expected, fixture.ScreenId, "[]", operation, default);
        Assert.IsTrue(lost.IsError);
        Assert.AreEqual(operation, Data(lost).GetProperty("operationId").GetString());
        Assert.AreEqual("operation_outcome_unknown", Data(lost).GetProperty("errorCode").GetString());
        Assert.AreEqual("indeterminate", Data(lost).GetProperty("outcome").GetString());
        Assert.IsTrue(Data(lost).GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual(1, fixture.Batches.Count);
        int captures = fixture.Captures;
        var recovered = await fixture.CacheTools.Replace(fixture.InstanceId, fixture.DocumentJson,
            expected, fixture.ScreenId, "[]", operation, default);
        Assert.IsFalse(recovered.IsError ?? false, Data(recovered).GetRawText());
        Assert.IsTrue(Data(recovered).GetProperty("replayed").GetBoolean());
        Assert.AreEqual(captures, fixture.Captures);
        Assert.AreEqual(1, fixture.Batches.Count);
    }

    [TestMethod]
    public async Task CompleteReadSnapshotRoundTripsAndKeepsLabelPropertiesForUpdate()
    {
        using var fixture = await Fixture.CreateAsync();
        var label = new LocalLabel
        {
            Id = new() { Value = Id }, Position = new() { XNm = 1_000, YNm = 2_000 },
            Text = new() { Text_ = "DATA" }, SpinStyle = SchematicLabelSpinStyle.SlssRight,
            Locked = LockedState.LsUnlocked, FieldsAutoplaced = true
        };
        label.Fields.Add(new SchematicField { Name = "field", Text = new() { Text_ = "kept" } });
        label.CustomProperties.Add(new CustomProperty { Key = "owner", Value = "agent" });
        fixture.Items.Add(Any.Pack(label));
        string expected = fixture.ExpectedJson;

        var read = await fixture.Tools.ReadConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, expected, default, SchematicConnectionItemKind.LocalLabel);
        Assert.IsFalse(read.IsError ?? false, Data(read).GetRawText());
        string json = Data(read).GetProperty("item").GetRawText();
        Any parsed = SchematicJson.Parser.Parse<Any>(json);
        Assert.IsTrue(parsed.Is(LocalLabel.Descriptor), json);
        var retained = parsed.Unpack<LocalLabel>();
        Assert.AreEqual(Id, retained.Id.Value);
        Assert.AreEqual("DATA", retained.Text.Text_);
        Assert.AreEqual("kept", retained.Fields.Single(f => f.Name == "field").Text.Text_);
        Assert.AreEqual("agent", retained.CustomProperties.Single(p => p.Key == "owner").Value);

        Assert.AreEqual(fixture.State.State.Document,
            SchematicJson.Parser.Parse<DocumentSpecifier>(Data(read).GetProperty("document").GetRawText()));
        Assert.AreEqual(fixture.TargetDocument,
            SchematicJson.Parser.Parse<DocumentSpecifier>(Data(read).GetProperty("targetDocument").GetRawText()));
        Assert.AreEqual(0, fixture.Batches.Count);

        string operation = Guid.NewGuid().ToString("D");
        var changed = await fixture.Tools.UpdateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, expected, operation, default, xNm: 3_000, yNm: 4_000, text: "DATA_OUT",
            expectedKind: SchematicConnectionItemKind.LocalLabel);
        Assert.IsFalse(changed.IsError ?? false, Data(changed).GetRawText());
        Assert.AreEqual(1, fixture.Batches.Count);
        var sent = fixture.Batches.Single().Batch.Operations.Single();
        Assert.AreEqual(fixture.TargetDocument, sent.TargetDocument);
        var update = sent.Update.Unpack<LocalLabel>();
        Assert.AreEqual("DATA_OUT", update.Text.Text_);
        Assert.AreEqual(new Vector2 { XNm = 3_000, YNm = 4_000 }, update.Position);
        update.Position = label.Position.Clone(); update.Text.Text_ = label.Text.Text_;
        Assert.AreEqual(label, update, "The typed patch must preserve every unedited native field.");
        Assert.AreEqual("DATA", retained.Text.Text_, "Reading and patching never mutate the client's old snapshot.");
        Assert.IsFalse(Data(changed).GetProperty("designFilesSaved").GetBoolean());

        int captures = fixture.Captures;
        var replay = await fixture.Tools.UpdateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, expected, operation, default, xNm: 3_000, yNm: 4_000, text: "DATA_OUT",
            expectedKind: SchematicConnectionItemKind.LocalLabel);
        Assert.IsFalse(replay.IsError ?? false, Data(replay).GetRawText());
        Assert.IsTrue(Data(replay).GetProperty("replayed").GetBoolean());
        Assert.IsFalse(Data(replay).GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual(Data(changed).GetProperty("receipt").GetRawText(), Data(replay).GetProperty("receipt").GetRawText());
        Assert.AreEqual(captures, fixture.Captures, "An exact replay must inspect its receipt before recapturing the now changed state.");
        Assert.AreEqual(1, fixture.Batches.Count);

        var rebound = await fixture.Tools.UpdateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, expected, operation, default, text: "DIFFERENT");
        AssertError(rebound, "native_status_3");
        Assert.AreEqual(1, fixture.Batches.Count, "A reused operation ID cannot be rebound to a different patch.");

        string deleteExpected = fixture.ExpectedJson;
        string deleteOperation = Guid.NewGuid().ToString("D");
        var deleted = await fixture.Tools.DeleteConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, deleteExpected, deleteOperation, default);
        Assert.IsFalse(deleted.IsError ?? false, Data(deleted).GetRawText());
        Assert.AreEqual(2, fixture.Batches.Count);
        Assert.AreEqual(Id, fixture.Batches[^1].Batch.Operations.Single().Remove.Value);
        Assert.IsEmpty(fixture.Items);
        captures = fixture.Captures;
        var deleteReplay = await fixture.Tools.DeleteConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, deleteExpected, deleteOperation, default);
        Assert.IsFalse(deleteReplay.IsError ?? false, Data(deleteReplay).GetRawText());
        Assert.IsTrue(Data(deleteReplay).GetProperty("replayed").GetBoolean());
        Assert.AreEqual(captures, fixture.Captures);
        Assert.AreEqual(2, fixture.Batches.Count, "A delete replay must work after the original item is gone.");
    }

    [TestMethod]
    public async Task TypedConnectionSnapshotRejectsUnsupportedGraphicsAndKeepsCanonicalIdentity()
    {
        using var fixture = await Fixture.CreateAsync();
        var graphic = Any.Pack(new SchematicLine
        {
            Id = new() { Value = Id }, Start = new() { XNm = 1_000, YNm = 2_000 },
            End = new() { XNm = 2_000, YNm = 2_000 }, Type = SchematicLineType.SltGraphic
        });
        string json = SchematicJson.Formatter.Format(graphic);
        Any parsed = SchematicJson.Parser.Parse<Any>(json);
        Assert.IsTrue(parsed.Is(SchematicLine.Descriptor));
        Assert.AreEqual(SchematicLineType.SltGraphic, parsed.Unpack<SchematicLine>().Type);
        Assert.AreEqual(Id, parsed.Unpack<SchematicLine>().Id.Value);
        fixture.Items.Add(parsed);
        AssertError(await fixture.Tools.ReadConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, fixture.ExpectedJson, default), "connection_item_not_found");
        AssertError(await fixture.Tools.UpdateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, fixture.ExpectedJson, Guid.NewGuid().ToString("D"), default, xNm: 3_000, yNm: 4_000), "connection_item_not_found");
        AssertError(await fixture.Tools.DeleteConnectionItem(fixture.InstanceId, fixture.DocumentJson,
            Id, fixture.ExpectedJson, Guid.NewGuid().ToString("D"), default), "connection_item_not_found");
        Assert.IsEmpty(fixture.Batches);
        Assert.AreEqual(0, fixture.Captures);
    }

    [TestMethod]
    public async Task ReadUpdateAndDeleteRefuseChangedCheckedStateBeforeMutation()
    {
        var changes = new Action<CheckedSchematicState>[]
        {
            state => { state.State.Revision.Sequence++; state.Electrical.Hierarchy.Revision.Sequence++; },
            state => state.State.StateSha256 = new string('c', 64),
            state => state.State.NativeIdentity = Guid.NewGuid().ToString("D"),
            state => state.State.FileBaselines[0].CurrentSha256 = new string('c', 64),
            state => state.Electrical.Hierarchy.Data.Instances[0].Items.Add(Any.Pack(new Junction
                { Id = new() { Value = Guid.NewGuid().ToString("D") }, Position = new() { XNm = 8_000, YNm = 9_000 } }))
        };
        foreach (var change in changes)
            foreach (string action in new[] { "read", "update", "delete" })
            {
                using var fixture = await Fixture.CreateAsync(); fixture.AddWire();
                string expected = fixture.ExpectedJson;
                change(fixture.State);
                AssertError(await fixture.Call(action, expected), "stale_document_state");
                Assert.AreEqual(1, fixture.Captures, action);
                Assert.IsEmpty(fixture.Batches, action);
            }
    }

    [TestMethod]
    public async Task ReadUpdateAndDeleteRejectOldEpochAndRecycledPeer()
    {
        foreach (string action in new[] { "read", "update", "delete" })
        {
            using var fixture = await Fixture.CreateAsync(); fixture.AddWire();
            var old = fixture.State.Clone(); old.State.ProcessEpoch = Guid.NewGuid().ToString("D");
            AssertError(await fixture.Call(action, SchematicJson.Formatter.Format(old)), "stale_process_epoch");
            Assert.AreEqual(0, fixture.Captures); Assert.AreEqual(0, fixture.Inspections);
            Assert.IsEmpty(fixture.Batches);

            fixture.Session.Epoch = Guid.NewGuid().ToString("D");
            AssertError(await fixture.Call(action, fixture.ExpectedJson), "instance_changed");
            Assert.AreEqual(0, fixture.Captures); Assert.AreEqual(0, fixture.Inspections);
            Assert.IsEmpty(fixture.Batches);
        }
    }

    [TestMethod]
    public async Task RejectedAndIndeterminateReceiptsRemainErrorsOnExactReplay()
    {
        foreach (var status in new[] { CheckedSchematicBatchStatus.CsbsRejected, CheckedSchematicBatchStatus.CsbsIndeterminate })
            foreach (string action in new[] { "update", "delete" })
            {
                using var fixture = await Fixture.CreateAsync(); fixture.AddWire(); fixture.Status = status;
                string expected = fixture.ExpectedJson;
                string operation = Guid.NewGuid().ToString("D");
                var first = await fixture.Call(action, expected, operation);
                Assert.IsTrue(first.IsError, Data(first).GetRawText());
                Assert.AreEqual(status, Receipt(first).Status);
                var replay = await fixture.Call(action, expected, operation);
                Assert.IsTrue(replay.IsError, Data(replay).GetRawText());
                Assert.AreEqual(status, Receipt(replay).Status);
                Assert.IsTrue(Data(replay).GetProperty("replayed").GetBoolean());
                Assert.AreEqual(1, fixture.Batches.Count); Assert.AreEqual(1, fixture.Captures);
                Assert.AreEqual(expected, fixture.ExpectedJson, "A refused or indeterminate receipt does not establish a new native state.");
            }
    }

    [TestMethod]
    public async Task InvalidPatchesAndMalformedNotFoundReceiptsNeverSubmitMutation()
    {
        using var fixture = await Fixture.CreateAsync(); fixture.AddWire();
        string expected = fixture.ExpectedJson;
        foreach (var patch in new (long? X, long? Y, long? EndX, long? EndY, string? Text, string Code)[]
        {
            (3_000, null, null, null, null, "invalid_connection_patch"),
            (null, null, 4_000, null, null, "invalid_connection_patch"),
            (3_001, 4_000, null, null, null, "invalid_coordinate"),
            (null, null, null, null, null, "empty_connection_patch"),
            (null, null, null, null, "", "invalid_connection_text"),
            (null, null, null, null, "wire text", "invalid_connection_patch")
        })
            AssertError(await fixture.Tools.UpdateConnectionItem(fixture.InstanceId, fixture.DocumentJson,
                Id, expected, Guid.NewGuid().ToString("D"), default,
                patch.X, patch.Y, patch.EndX, patch.EndY, patch.Text), patch.Code);
        Assert.IsEmpty(fixture.Batches); Assert.AreEqual(0, fixture.Captures); Assert.AreEqual(0, fixture.Inspections);

        fixture.BadNotFoundReceipt = true;
        AssertError(await fixture.Call("update", expected), "invalid_checked_batch");
        Assert.IsEmpty(fixture.Batches); Assert.AreEqual(0, fixture.Captures);
    }

    private static JsonElement Data(CallToolResult result) => JsonSerializer.SerializeToElement(result.StructuredContent);
    private static CheckedSchematicBatchReceipt Receipt(CallToolResult result) =>
        SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(Data(result).GetProperty("receipt").GetRawText());
    private static void AssertError(CallToolResult result, string code)
    {
        Assert.IsTrue(result.IsError, Data(result).GetRawText());
        Assert.AreEqual(code, Data(result).GetProperty("errorCode").GetString(), Data(result).GetRawText());
        Assert.IsFalse(Data(result).GetProperty("mutationSubmitted").GetBoolean());
    }

    // Explicit protocol fixture: this exercises the compiled tool boundary, not a running native KiCad.
    private sealed class Fixture : INativeTransport, IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("schematic-typed-tool-").FullName;
        private readonly Dictionary<string, (CheckedSchematicBatch Request, CheckedSchematicBatchReceipt Receipt)> receipts = [];
        internal NativeClientTests.FixtureTransport Session { get; } = new() { Epoch = Guid.NewGuid().ToString("D") };
        internal CheckedSchematicState State { get; }
        internal DocumentSpecifier TargetDocument => State.Electrical.Hierarchy.Data.Instances[0].Metadata.Document;
        internal Google.Protobuf.Collections.RepeatedField<Any> Items => State.Electrical.Hierarchy.Data.Instances[0].Items;
        internal SchematicTypedMutationTools Tools { get; }
        internal SchematicLibraryCacheTools CacheTools { get; }
        internal string InstanceId => Session.InstanceId;
        internal string DocumentJson => SchematicJson.Formatter.Format(State.State.Document);
        internal string ExpectedJson => SchematicJson.Formatter.Format(State);
        internal string ScreenId { get; }
        internal List<CheckedSchematicBatch> Batches { get; } = [];
        internal int Captures { get; private set; }
        internal int Inspections { get; private set; }
        internal CheckedSchematicBatchStatus Status { get; set; } = CheckedSchematicBatchStatus.CsbsCompleted;
        internal bool BadNotFoundReceipt { get; set; }
        internal bool TamperCacheReceipt { get; set; }
        internal NngException? CacheTransportFailure { get; set; }
        private InstanceRegistry Registry { get; }

        private Fixture()
        {
            Session.ProjectPath = Path.Combine(root, "fixture.kicad_pro");
            Registry = new(this, root); Tools = new(Registry); CacheTools = new(Registry);
            var checkedRequest = CheckedSchematicToolTests.Request(root, Session.Epoch);
            State = new() { State = checkedRequest.ExpectedState.Clone(), Electrical = new()
                { Hierarchy = new() { Data = new() { Document = checkedRequest.Batch.Document.Clone() },
                    Revision = checkedRequest.Batch.ExpectedRevision.Clone() } } };
            var child = State.State.Document.Clone(); child.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            ScreenId = Guid.NewGuid().ToString("D");
            State.Electrical.Hierarchy.Data.Instances.Add(new SchematicScreenData
            {
                Metadata = new() { Document = child, ScreenId = new() { Value = ScreenId } }
            });
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                await fixture.Registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(fixture.root, "native.sock")), fixture.InstanceId);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        internal void AddWire() => Items.Add(Any.Pack(SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Wire, Id, 1_000, 2_000, 4_000, 2_000, null)));

        internal Task<CallToolResult> Call(string action, string expected, string? operation = null) => action switch
        {
            "read" => Tools.ReadConnectionItem(InstanceId, DocumentJson, Id, expected, default),
            "update" => Tools.UpdateConnectionItem(InstanceId, DocumentJson, Id, expected,
                operation ?? Guid.NewGuid().ToString("D"), default, xNm: 3_000, yNm: 4_000),
            "delete" => Tools.DeleteConnectionItem(InstanceId, DocumentJson, Id, expected,
                operation ?? Guid.NewGuid().ToString("D"), default),
            _ => throw new ArgumentException("Unknown fixture action", nameof(action))
        };

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] requestBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var request = ApiRequest.Parser.ParseFrom(requestBytes);
            IMessage payload;
            if (request.Message.Is(ReadCheckedSchematicState.Descriptor))
            {
                Captures++;
                var query = request.Message.Unpack<ReadCheckedSchematicState>();
                Assert.AreEqual(State.State.Document, query.Document); Assert.AreEqual(Session.Epoch, query.ProcessEpoch);
                payload = State.Clone();
            }
            else if (request.Message.Is(ReadCheckedSchematicBatchReceipt.Descriptor))
            {
                Inspections++;
                var query = request.Message.Unpack<ReadCheckedSchematicBatchReceipt>();
                Assert.AreEqual(Session.Epoch, query.ProcessEpoch);
                if (receipts.TryGetValue(query.OperationId, out var saved))
                {
                    if (!saved.Request.Equals(query.ExpectedRequest))
                        return Reply(new CheckedSchematicBatchReceipt(), 3, "operation_reused");
                    payload = saved.Receipt.Clone();
                }
                else payload = new CheckedSchematicBatchReceipt
                {
                    Document = query.Document.Clone(), OperationId = query.OperationId,
                    ProcessEpoch = BadNotFoundReceipt ? Guid.NewGuid().ToString("D") : Session.Epoch,
                    Status = CheckedSchematicBatchStatus.CsbsNotFound
                };
            }
            else if (request.Message.Is(CheckedSchematicBatch.Descriptor))
            {
                var batch = request.Message.Unpack<CheckedSchematicBatch>();
                if (receipts.TryGetValue(batch.Batch.OperationId, out var replay))
                {
                    Assert.AreEqual(replay.Request, batch, "A reused operation ID must remain bound to the exact request.");
                    return Reply(replay.Receipt.Clone());
                }
                Batches.Add(batch.Clone());
                CheckedSchematicContract.ValidateRequest(batch, Session.Epoch);
                var result = new CheckedSchematicBatchReceipt
                {
                    Document = batch.Batch.Document.Clone(), OperationId = batch.Batch.OperationId,
                    ProcessEpoch = Session.Epoch, ExpectedRequestVerified = true, Status = Status,
                    ObservedBefore = State.State.Clone()
                };
                Assert.AreEqual(batch.ExpectedState, State.State);
                if (Status == CheckedSchematicBatchStatus.CsbsCompleted)
                {
                    var operation = batch.Batch.Operations.Single();
                    if (operation.TargetDocument is { } targetDocument)
                    {
                        if (operation.ReplaceLibraryCache is null) Assert.AreEqual(TargetDocument, targetDocument);
                        else Assert.IsTrue(State.Electrical.Hierarchy.Data.Instances.Any(i => i.Metadata.Document.Equals(targetDocument)));
                    }
                    if (operation.ReplaceLibraryCache is not null)
                    {
                        Assert.AreEqual(ScreenId, operation.ReplaceLibraryCache.ScreenId.Value);
                        foreach (var screen in State.Electrical.Hierarchy.Data.Instances.Where(i => i.Metadata.ScreenId.Value == ScreenId))
                        {
                            screen.CachedSymbols.Clear(); screen.CachedSymbols.Add(operation.ReplaceLibraryCache.Definitions.Select(d => d.Clone()));
                        }
                        result.Result = new() { Revision = State.State.Revision.Clone(), LibraryCacheChanged = true };
                    }
                    else if (operation.Create is not null) Items.Add(operation.Create.Clone());
                    else if (operation.Update is not null) Items[0] = operation.Update.Clone();
                    else { Assert.AreEqual(Id, operation.Remove.Value); Items.Clear(); }
                    State.State.Revision.Sequence++; State.Electrical.Hierarchy.Revision = State.State.Revision.Clone();
                    State.State.StateSha256 = new string('d', 64);
                    result.ObservedAfter = State.State.Clone();
                    if (operation.ReplaceLibraryCache is not null)
                        result.Result.Revision = State.State.Revision.Clone();
                    else
                    {
                        result.Result = new() { Revision = State.State.Revision.Clone() };
                        if (operation.Create is not null) result.Result.Items.Add(operation.Create.Clone());
                        else if (operation.Update is not null) result.Result.Items.Add(operation.Update.Clone());
                        else result.Result.Removed.Add(operation.Remove.Clone());
                    }
                }
                else { result.ErrorCode = "fixture_failure"; result.ObservedAfter = State.State.Clone(); }
                receipts[batch.Batch.OperationId] = (batch.Clone(), result.Clone());
                if (CacheTransportFailure is { } transportError && batch.Batch.Operations.Single().ReplaceLibraryCache is not null)
                {
                    CacheTransportFailure = null;
                    return Task.FromException<byte[]>(transportError);
                }
                if (TamperCacheReceipt && batch.Batch.Operations.Single().ReplaceLibraryCache is not null)
                    result.OperationId = Guid.NewGuid().ToString("D");
                payload = result;
            }
            else return Session.ExchangeAsync(endpoint, requestBytes, timeout, cancellationToken);
            return Reply(payload);
        }

        private Task<byte[]> Reply(IMessage payload, int status = 1, string error = "") => Task.FromResult(new ApiResponse
        {
            Header = new() { KicadToken = Session.Epoch }, Status = new() { Status = (ApiStatusCode)status, ErrorMessage = error },
            Message = Any.Pack(payload)
        }.ToByteArray());

        public void Dispose() => Directory.Delete(root, true);
    }
}
