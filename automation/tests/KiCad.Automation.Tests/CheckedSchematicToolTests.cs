using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CheckedSchematicToolTests
{
    [TestMethod]
    public async Task InvalidInputAndCancellationDoNotDispatch()
    {
        string root = Directory.CreateTempSubdirectory("checked-batch-input-").FullName;
        try
        {
            var transport = new Transport(); var registry = new InstanceRegistry(transport, root);
            var tools = new CheckedSchematicTools(registry);
            foreach (string json in new[] { "{", "{}", "null" })
                Assert.IsTrue((await tools.Apply("missing", json, default)).IsError);
            await Assert.ThrowsAsync<OperationCanceledException>(() => tools.Apply("missing", "{}", new CancellationToken(true)));
            await Assert.ThrowsAsync<OperationCanceledException>(() => tools.Inspect("missing", "{}", new CancellationToken(true)));
            Assert.AreEqual(0, transport.Mutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeReceiptsRemainTypedAndInspectionNeverResubmitsMutation()
    {
        string root = Directory.CreateTempSubdirectory("checked-batch-results-").FullName;
        try
        {
            var transport = new Transport(); transport.Session.ProjectPath = Path.Combine(root, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.Session.InstanceId);
            var tool = new CheckedSchematicTools(registry);
            foreach (var status in new[] { CheckedSchematicBatchStatus.CsbsCompleted, CheckedSchematicBatchStatus.CsbsRejected, CheckedSchematicBatchStatus.CsbsIndeterminate })
            {
                var request = Request(root, transport.Session.Epoch); transport.Status = status;
                string json = SchematicJson.Formatter.Format(request);
                var response = await tool.Apply(transport.Session.InstanceId, json, default);
                Assert.AreEqual(status != CheckedSchematicBatchStatus.CsbsCompleted, response.IsError ?? false);
                var data = JsonSerializer.SerializeToElement(response.StructuredContent);
                Assert.AreEqual(status, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(data.GetProperty("receipt").GetRawText()).Status);
                int calls = transport.Mutations;
                Assert.IsFalse((await tool.Inspect(transport.Session.InstanceId, json, default)).IsError ?? false);
                Assert.AreEqual(calls, transport.Mutations);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UnsupportedPeerIsNotRetriedThroughAnUncheckedLegacyBatch()
    {
        string root = Directory.CreateTempSubdirectory("checked-batch-old-peer-").FullName;
        try
        {
            var transport = new Transport { Unsupported = true }; transport.Session.ProjectPath = Path.Combine(root, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.Session.InstanceId);
            var response = await new CheckedSchematicTools(registry).Apply(transport.Session.InstanceId,
                SchematicJson.Formatter.Format(Request(root, transport.Session.Epoch)), default);
            Assert.IsTrue(response.IsError);
            var data = JsonSerializer.SerializeToElement(response.StructuredContent);
            Assert.AreEqual("not_confirmed", data.GetProperty("outcome").GetString());
            Assert.AreEqual(1, transport.Mutations); Assert.AreEqual(0, transport.LegacyMutations);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CompletedRepliesRequireConsistentStateAndCompleteDiskCoverage()
    {
        string root = Path.GetTempPath(); var request = Request(root, Guid.NewGuid().ToString("D"));
        var baseline = Result(request, CheckedSchematicBatchStatus.CsbsCompleted);
        CheckedSchematicTools.ValidateResult(request, baseline, false);
        foreach (int invalid in Enumerable.Range(0, 7))
        {
            var response = baseline.Clone();
            switch (invalid)
            {
                case 0: response.ObservedAfter.StateSha256 = "bad"; break;
                case 1: response.ObservedAfter.FileBaselines.Clear(); break;
                case 2: response.ObservedAfter.FileBaselines[0].CurrentSha256 = new string('c', 64); break;
                case 3: response.Result.Revision.Sequence--; break;
                case 4: response.ExpectedRequestVerified = false; break;
                case 5: response.ObservedAfter.ProcessEpoch = Guid.NewGuid().ToString("D"); break;
                case 6: response.ErrorCode = "failure"; break;
            }
            Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => CheckedSchematicTools.ValidateResult(request, response, false));
        }
        var absent = Result(request, CheckedSchematicBatchStatus.CsbsNotFound);
        absent.ExpectedRequestVerified = false;
        CheckedSchematicTools.ValidateResult(request, absent, true);
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => CheckedSchematicTools.ValidateResult(request, absent, false));
        absent.ObservedBefore = request.ExpectedState.Clone();
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => CheckedSchematicTools.ValidateResult(request, absent, true));
        var rejected = Result(request, CheckedSchematicBatchStatus.CsbsRejected);
        rejected.ObservedBefore = request.ExpectedState.Clone(); rejected.ObservedAfter = request.ExpectedState.Clone();
        rejected.ObservedAfter.StateSha256 = new string('f', 64);
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => CheckedSchematicTools.ValidateResult(request, rejected, false));
    }

    [TestMethod]
    public void CombinedObservationRejectsMixedRevisionAndProcessTargets()
    {
        var request = Request(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        var result = new CheckedSchematicState { State = request.ExpectedState.Clone(), Electrical = new()
        { Hierarchy = new() { Data = new() { Document = request.Batch.Document.Clone() }, Revision = request.Batch.ExpectedRevision.Clone() } } };
        CheckedSchematicContract.ValidateObservation(result, request.Batch.Document, request.ExpectedState.ProcessEpoch);
        var projected = result.Clone();
        projected.Electrical.Hierarchy.Data.Instances.Add(new Kiapi.Schematic.Types.SchematicScreenData { Metadata = new() });
        projected.Electrical.Hierarchy.Data.Instances[0].Metadata.UnrepresentedState.Add("net_settings_require_snapshot_schema_9");
        Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() =>
            CheckedSchematicContract.ValidateObservation(projected, request.Batch.Document, request.ExpectedState.ProcessEpoch));
        projected.Electrical.Hierarchy.Data.Instances[0].Metadata.UnrepresentedState.Clear();
        projected.Electrical.Hierarchy.Data.Instances[0].Metadata.UnrepresentedState.Add("unsupported_future_graphic");
        CheckedSchematicContract.ValidateObservation(projected, request.Batch.Document, request.ExpectedState.ProcessEpoch);
        foreach (int invalid in Enumerable.Range(0, 4))
        {
            var changed = result.Clone();
            if (invalid == 0) changed.Electrical.Hierarchy.Revision.Sequence++;
            if (invalid == 1) changed.State.ProcessEpoch = Guid.NewGuid().ToString("D");
            if (invalid == 2) changed.State.StateSha256 = "bad";
            if (invalid == 3) changed.Electrical.Hierarchy.Data.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
            Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() =>
                CheckedSchematicContract.ValidateObservation(changed, request.Batch.Document, request.ExpectedState.ProcessEpoch));
        }
    }

    // The MCP side of kicad_schematic_checked_view: KiCad's reply is published only when the checked state, the image and
    // the objects all belong to one checkpoint of this process, root and viewed sheet, and the image is the PNG it claims.
    // Isolated because a real KiCad never returns these inconsistent replies; NativeSessionTests.
    // AgentAndPersonEditingTogetherNeverGetStaleOrPartialEdits covers the consistent path end to end.
    [TestMethod]
    public void CheckedViewRejectsAnImageOrObjectsOfAnotherRevisionSheetOrFormat()
    {
        var request = Request(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        var root = request.Batch.Document;
        var child = root.Clone(); child.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        foreach (var sheet in new[] { root, child })
        {
            var view = View(request, sheet);
            CheckedSchematicTools.ValidateView(view, root, sheet, request.ExpectedState.ProcessEpoch);
            var cases = new (string Name, Action<NativeCapabilityCheckedView> Change)[]
            {
                ("image of another revision", v => v.View.Preview.Revision.Sequence++),
                ("objects of another revision", v => v.View.Snapshot.Revision.Sequence++),
                ("image of another epoch", v => v.View.Preview.Revision.Epoch = Guid.NewGuid().ToString("D")),
                ("image of another sheet", v => v.View.Preview.Document = Other(sheet)),
                ("objects of another sheet", v => v.View.Snapshot.Data.Metadata.Document = Other(sheet)),
                ("state of another root", v => { v.Checked.State.Document = Other(root); v.Checked.Electrical.Hierarchy.Data.Document = Other(root); }),
                ("state of another process", v => v.Checked.State.ProcessEpoch = Guid.NewGuid().ToString("D")),
                ("width other than the image's", v => v.View.Preview.WidthPixels++),
                ("height other than the image's", v => v.View.Preview.HeightPixels--),
                ("image that is not a PNG", v => v.View.Preview.Png = ByteString.CopyFrom(Png(v.View.Preview.WidthPixels, v.View.Preview.HeightPixels, jpeg: true))),
                ("image too short to be a PNG", v => v.View.Preview.Png = ByteString.CopyFrom(Png(v.View.Preview.WidthPixels, v.View.Preview.HeightPixels)[..20])),
                ("no image", v => v.View.Preview = null),
                ("no objects", v => v.View.Snapshot = null),
                ("no checked state", v => v.Checked = null)
            };
            foreach (var (name, change) in cases)
            {
                var invalid = view.Clone();
                change(invalid);
                var error = Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(
                    () => CheckedSchematicTools.ValidateView(invalid, root, sheet, request.ExpectedState.ProcessEpoch), name);
                Assert.IsTrue(error.Code is "invalid_checked_view" or "invalid_checked_batch", $"{name}: {error.Code}");
            }
        }
    }

    // The tool publishes a consistent view as its image block plus the structured view without the image bytes, and
    // refuses an inconsistent reply as invalid_checked_view without any image. One native request each time.
    [TestMethod]
    public async Task CheckedViewPublishesOnlyAConsistentReply()
    {
        string root = Directory.CreateTempSubdirectory("checked-view-results-").FullName;
        try
        {
            var transport = new Transport(); transport.Session.ProjectPath = Path.Combine(root, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.Session.InstanceId);
            var tool = new CheckedSchematicTools(registry);
            var request = Request(root, transport.Session.Epoch);
            var document = request.Batch.Document;
            var child = document.Clone(); child.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            string documentJson = SchematicJson.Formatter.Format(document), childJson = SchematicJson.Formatter.Format(child);

            transport.View = View(request, child);
            var shown = await tool.ObserveView(transport.Session.InstanceId, documentJson, default, childJson);
            Assert.IsFalse(shown.IsError ?? false, JsonSerializer.Serialize(shown.StructuredContent));
            Assert.AreEqual(1, transport.Views);
            Assert.AreEqual(child, transport.LastView!.View, "The tool asks KiCad for exactly the sheet the agent named.");
            Assert.AreEqual(document, transport.LastView.Document);
            Assert.AreEqual(transport.Session.Epoch, transport.LastView.ProcessEpoch);
            var image = (ModelContextProtocol.Protocol.ImageContentBlock)shown.Content[0];
            Assert.AreEqual("image/png", image.MimeType);
            CollectionAssert.AreEqual(transport.View.View.Preview.Png.ToByteArray(), image.DecodedData.ToArray());
            var published = SchematicJson.Parser.Parse<NativeCapabilityCheckedView>(JsonSerializer.Serialize(shown.StructuredContent));
            Assert.IsTrue(published.View.Preview.Png.IsEmpty, "The structured view leaves the image to its image block.");
            var expected = transport.View.Clone(); expected.View.Preview.Png = ByteString.Empty;
            Assert.AreEqual(expected, published);

            transport.View = View(request, child);
            transport.View.View.Preview.Revision.Sequence++;
            var refused = await tool.ObserveView(transport.Session.InstanceId, documentJson, default, childJson);
            Assert.IsTrue(refused.IsError);
            Assert.AreEqual("invalid_checked_view", JsonSerializer.SerializeToElement(refused.StructuredContent).GetProperty("code").GetString());
            Assert.IsFalse(refused.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>().Any(), "A refused view carries no image.");
            Assert.AreEqual(2, transport.Views);
        }
        finally { Directory.Delete(root, true); }
    }

    // A consistent checked view of the given sheet for the state the request expects.
    private static NativeCapabilityCheckedView View(CheckedSchematicBatch request, DocumentSpecifier sheet)
    {
        var revision = request.ExpectedState.Revision;
        var checkedState = new CheckedSchematicState { State = request.ExpectedState.Clone(), Electrical = new()
            { Hierarchy = new() { Data = new() { Document = request.Batch.Document.Clone() }, Revision = revision.Clone() } } };
        const uint Width = 640, Height = 480;
        return new()
        {
            Checked = checkedState,
            View = new()
            {
                Snapshot = new() { Revision = revision.Clone(), Data = new() { Metadata = new() { Document = sheet.Clone() } } },
                Preview = new() { Revision = revision.Clone(), Document = sheet.Clone(), WidthPixels = Width, HeightPixels = Height,
                    Png = ByteString.CopyFrom(Png(Width, Height)) }
            }
        };
    }

    // The signature and IHDR header of an image of the given size, or a JPEG's first bytes in their place.
    private static byte[] Png(uint width, uint height, bool jpeg = false)
    {
        var bytes = new byte[33];
        byte[] signature = jpeg ? new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F' } : new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        signature.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    // The same kind of target with another sheet-instance path.
    private static DocumentSpecifier Other(DocumentSpecifier document)
    {
        var other = document.Clone();
        other.SheetPath.Path[^1].Value = Guid.NewGuid().ToString("D");
        return other;
    }

    internal static CheckedSchematicBatch Request(string root, string processEpoch)
    {
        var state = new DocumentLifecycleState
        {
            Document = new() { Type = (DocumentType)1, SheetPath = new(), Project = new() { Name = "fixture", Path = root } },
            ProcessEpoch = processEpoch, NativeIdentity = Guid.NewGuid().ToString("D"),
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 7 }, StateSha256 = new string('a', 64),
            Scope = DocumentLifecycleScope.DlsSchematicHierarchy, ProjectSettingsIncluded = true
        };
        state.Document.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        string file = Path.Combine(root, "fixture.kicad_sch"); state.NativeFiles.Add(file);
        state.FileBaselines.Add(new NativeFileBaselineState { Path = file, BaselinePath = file, BaselineKnown = true, CurrentKnown = true,
            BaselineExists = true, CurrentExists = true, BaselineSha256 = new string('b', 64), CurrentSha256 = new string('b', 64),
            BaselineBytes = 3, CurrentBytes = 3, Status = NativeFileBaselineStatus.NfbsUnchanged });
        var batch = new ApplySchematicItemBatch { Document = state.Document.Clone(), ExpectedRevision = state.Revision.Clone(),
            DocumentEpoch = state.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        batch.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = "Checked edit" } });
        return new() { Batch = batch, ExpectedState = state };
    }

    private static CheckedSchematicBatchReceipt Result(CheckedSchematicBatch request, CheckedSchematicBatchStatus status)
    {
        var result = new CheckedSchematicBatchReceipt { Document = request.Batch.Document.Clone(), ProcessEpoch = request.ExpectedState.ProcessEpoch,
            OperationId = request.Batch.OperationId, ExpectedRequestVerified = status != CheckedSchematicBatchStatus.CsbsNotFound, Status = status };
        if (status == CheckedSchematicBatchStatus.CsbsCompleted)
        {
            result.ObservedBefore = request.ExpectedState.Clone(); result.ObservedAfter = request.ExpectedState.Clone();
            result.ObservedAfter.Revision.Sequence++;
            result.ObservedAfter.StateSha256 = new string('d', 64);
            result.Result = new() { Revision = result.ObservedAfter.Revision.Clone() };
        }
        else if (status != CheckedSchematicBatchStatus.CsbsNotFound) result.ErrorCode = "fixture_failure";
        return result;
    }

    private sealed class Transport : INativeTransport
    {
        internal NativeClientTests.FixtureTransport Session { get; } = new() { Epoch = Guid.NewGuid().ToString("D") };
        internal int Mutations { get; private set; }
        internal int LegacyMutations { get; private set; }
        internal bool Unsupported { get; init; }
        internal CheckedSchematicBatchStatus Status { get; set; } = CheckedSchematicBatchStatus.CsbsCompleted;
        // What KiCad answers to a checked view request, and the requests it received.
        internal NativeCapabilityCheckedView? View { get; set; }
        internal NativeCapabilityReadCheckedView? LastView { get; private set; }
        internal int Views { get; private set; }
        private CheckedSchematicBatchReceipt? receipt;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(bytes);
            if (envelope.Message.Is(NativeCapabilityReadCheckedView.Descriptor))
            {
                Views++;
                LastView = envelope.Message.Unpack<NativeCapabilityReadCheckedView>();
                return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(View!) }.ToByteArray());
            }
            if (envelope.Message.Is(ApplySchematicItemBatch.Descriptor)) LegacyMutations++;
            if (envelope.Message.Is(CheckedSchematicBatch.Descriptor))
            {
                Mutations++;
                if (Unsupported) return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                    Status = new() { Status = (ApiStatusCode)5, ErrorMessage = "Unsupported checked command" } }.ToByteArray());
                receipt = Result(envelope.Message.Unpack<CheckedSchematicBatch>(), Status);
            }
            else if (!envelope.Message.Is(ReadCheckedSchematicBatchReceipt.Descriptor))
                return Session.ExchangeAsync(endpoint, bytes, timeout, cancellationToken);
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(receipt!) }.ToByteArray());
        }
    }
}
