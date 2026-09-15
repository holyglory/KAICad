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
        private CheckedSchematicBatchReceipt? receipt;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] bytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(bytes);
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
