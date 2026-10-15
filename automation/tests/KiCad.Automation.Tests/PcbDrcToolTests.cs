using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbDrcToolTests
{
    [TestMethod]
    public async Task JobControlsRoundTripStructuredStateAndRejectStaleEpoch()
    {
        string directory = Directory.CreateTempSubdirectory("drc-job-contract-").FullName;
        try
        {
            var transport = new JobTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new PcbDrcTools(registry);
            var document = new DocumentSpecifier { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = directory } };
            transport.State = new PcbDrcJobState
            {
                Document = document.Clone(),
                JobId = Guid.NewGuid().ToString("D"),
                OperationId = Guid.NewGuid().ToString("D"),
                ProcessEpoch = transport.Session.Epoch,
                CheckedRevision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 8 },
                Status = (PcbDrcJobStatus)3,
                Progress = 1,
                ResultsFresh = true, WorkerFinished = true, SnapshotComplete = true
            };
            string json = SchematicJson.Formatter.Format(document);
            string revisionJson = SchematicJson.Formatter.Format(transport.State.CheckedRevision);
            var started = await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default);
            Assert.IsFalse(started.IsError ?? false, started.ToString());
            Assert.IsTrue(started.StructuredContent.HasValue);
            Assert.AreEqual(transport.State,
                SchematicJson.Parser.Parse<PcbDrcJobState>(((TextContentBlock)started.Content.Single()).Text));
            var candidateRequest = new CreateItems { Header = new() { Document = document } };
            candidateRequest.Items.Add(Any.Pack(new Track
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Start = new() { XNm = 1_000_000, YNm = 1_000_000 },
                End = new() { XNm = 2_000_000, YNm = 1_000_000 },
                Width = new() { ValueNm = 250_000 }, Layer = BoardLayer.BlFCu,
                Net = new() { Name = "POWER_RAIL" }
            }));
            string candidateOperationId = Guid.NewGuid().ToString("D");
            transport.State.OperationId = candidateOperationId;
            transport.State.CandidateDryRun = true;
            transport.State.CandidateItemIds.Add(candidateRequest.Items[0].Unpack<Track>().Id.Value);
            var candidateStarted = await tool.Start(transport.Session.InstanceId, json, candidateOperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default,
                candidateRequestJson: BoardJson.Formatter.Format(candidateRequest));
            Assert.IsFalse(candidateStarted.IsError ?? false, candidateStarted.ToString());
            Assert.AreEqual(1, transport.LastStart!.CandidateItems.Count);
            Assert.IsTrue(SchematicJson.Parser.Parse<PcbDrcJobState>(
                ((TextContentBlock)candidateStarted.Content.Single()).Text).CandidatePayloadVerified,
                "A candidate start must return the exact native payload receipt.");
            Assert.IsTrue(PcbDrcTools.TryGetCandidateReceipt(transport.State.JobId, transport.Session.Epoch,
                out string candidateEpoch, out DocumentSpecifier candidateDocument,
                out DocumentRevision candidateRevision, out string candidateHash,
                out IReadOnlyList<string> candidateDeleteIds));
            Assert.AreEqual(transport.Session.Epoch, candidateEpoch);
            Assert.AreEqual(document, candidateDocument);
            Assert.AreEqual(transport.State.CheckedRevision, candidateRevision);
            Assert.AreEqual(PcbDrcTools.CanonicalCandidate(candidateRequest), candidateHash);
            Assert.IsEmpty(candidateDeleteIds);
            // The verification read is a receipt boundary: a well-formed job
            // from another native identity must never be promoted to this
            // candidate's receipt.
            string mismatchedOperationId = Guid.NewGuid().ToString("D");
            transport.State.OperationId = mismatchedOperationId;
            transport.ReplacementState = transport.State.Clone();
            transport.ReplacementState.JobId = Guid.NewGuid().ToString("D");
            var mismatched = await tool.Start(transport.Session.InstanceId, json, mismatchedOperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default,
                candidateRequestJson: BoardJson.Formatter.Format(candidateRequest));
            Assert.IsTrue(mismatched.IsError ?? false, mismatched.ToString());
            transport.ReplacementState = null;
            transport.State.CandidateDryRun = false;
            transport.State.CandidateItemIds.Clear();
            var read = await tool.Job(transport.Session.InstanceId, json, transport.State.JobId,
                transport.Session.Epoch, default);
            Assert.IsFalse(read.IsError ?? false, read.ToString());
            var cancelled = await tool.Cancel(transport.Session.InstanceId, json, transport.State.JobId,
                transport.Session.Epoch, default);
            Assert.IsFalse(cancelled.IsError ?? false, cancelled.ToString());
            Assert.AreEqual(7, transport.Calls);
            var stale = await tool.Job(transport.Session.InstanceId, json, transport.State.JobId,
                Guid.NewGuid().ToString("D"), default);
            Assert.IsTrue(stale.IsError ?? false);
            Assert.AreEqual(7, transport.Calls);
            var badOperation = await tool.Start(transport.Session.InstanceId, json,
                Guid.Empty.ToString("D"), false, false, false, revisionJson, transport.Session.Epoch, default);
            Assert.IsTrue(badOperation.IsError ?? false);
            Assert.AreEqual(7, transport.Calls);
            int beforeNonCanonical = transport.Calls;
            var uppercaseOperation = await tool.Start(transport.Session.InstanceId, json,
                "A2222222-2222-2222-2222-222222222222", false, false, false,
                revisionJson, transport.Session.Epoch, default);
            Assert.IsTrue(uppercaseOperation.IsError ?? false,
                "DRC operation IDs must be rejected rather than normalized from uppercase text.");
            Assert.AreEqual(beforeNonCanonical, transport.Calls);
            var uppercaseJob = await tool.Job(transport.Session.InstanceId, json,
                "B2222222-2222-2222-2222-222222222222", transport.Session.Epoch, default);
            Assert.IsTrue(uppercaseJob.IsError ?? false,
                "DRC job IDs must be rejected rather than normalized from uppercase text.");
            Assert.AreEqual(beforeNonCanonical, transport.Calls);
            var malformedCandidate = await tool.Job(transport.Session.InstanceId, json, transport.State.JobId,
                transport.Session.Epoch, default, candidateRequestJson: "{");
            Assert.IsTrue(malformedCandidate.IsError ?? false,
                "A malformed candidate retry must fail before native dispatch.");
            Assert.AreEqual(7, transport.Calls);
            var malformedDelete = await tool.Job(transport.Session.InstanceId, json, transport.State.JobId,
                transport.Session.Epoch, default, deleteRequestJson: "{");
            Assert.IsTrue(malformedDelete.IsError ?? false,
                "A malformed delete retry must fail before native dispatch.");
            // Candidate starts perform a native replacement-read verification
            // after the start reply.  Both malformed retries are rejected
            // before dispatch, so the seven calls already made remain intact.
            Assert.AreEqual(7, transport.Calls);
            var valid = transport.State.Clone();
            for (int variant = 0; variant < 15; ++variant)
            {
                transport.State = valid.Clone();
                switch (variant)
                {
                    case 0: transport.State.JobId = Guid.NewGuid().ToString("D"); break;
                    case 1: transport.State.WorkerFinished = false; break;
                    case 2: transport.State.SnapshotComplete = false; break;
                    case 3: transport.State.Progress = 0; break;
                    case 4: transport.State.Status = (PcbDrcJobStatus)4; break;
                    case 5: transport.State.OperationId = Guid.Empty.ToString("D"); break;
                    case 6: transport.State.CheckedRevision.Epoch = Guid.Empty.ToString("D"); break;
                    // An incomplete snapshot must say why, and a complete one must not claim a gap.
                    case 7: transport.State.SnapshotComplete = false; transport.State.ResultsFresh = false; break;
                    case 8: transport.State.InputWarnings.Add("snapshot_incomplete: item_identity: fixture"); break;
                    // A running check has not yet matched its findings to the open board, so it never claims completeness.
                    case 9:
                        transport.State.Status = (PcbDrcJobStatus)2; transport.State.WorkerFinished = false;
                        transport.State.Progress = 0.2; transport.State.ResultsFresh = false;
                        break;
                    case 10:
                        transport.State.Status = (PcbDrcJobStatus)5; transport.State.WorkerFinished = true;
                        transport.State.SnapshotComplete = true; transport.State.ResultsFresh = false;
                        transport.State.ErrorCode = ""; transport.State.ErrorMessage = "";
                        break;
                    case 11:
                        transport.State.Status = (PcbDrcJobStatus)6; transport.State.WorkerFinished = true;
                        transport.State.SnapshotComplete = true; transport.State.ResultsFresh = false;
                        transport.State.CancellationRequested = false;
                        transport.State.ErrorCode = "document_changed";
                        transport.State.ErrorMessage = "The live PCB changed while DRC was running";
                        break;
                    case 12:
                        transport.State.JobId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
                        break;
                    case 13:
                        transport.State.OperationId = "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB";
                        break;
                    case 14:
                        transport.State.CheckedRevision.Epoch = "CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC";
                        break;
                }
                Assert.IsTrue((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                    transport.Session.Epoch, default)).IsError, $"Invalid native state variant {variant} was accepted");
            }

            // A dependency notification or checkpoint can invalidate a completed
            // receipt for several distinct native reasons. Preserve the reason as
            // a stale terminal state, while refusing arbitrary worker failures or
            // a completed state that still claims cancellation/freshness.
            foreach (string code in new[]
            {
                "document_changed", "project_inputs_changed", "library_inputs_changed",
                "auxiliary_inputs_changed", "schematic_changed", "input_events_lost"
            })
            {
                transport.State = valid.Clone();
                transport.State.Status = (PcbDrcJobStatus)6;
                transport.State.ResultsFresh = false;
                transport.State.CancellationRequested = true;
                transport.State.ErrorCode = code;
                transport.State.ErrorMessage = "The captured DRC dependency changed.";
                Assert.IsFalse((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                    transport.Session.Epoch, default)).IsError ?? false,
                    $"Native dependency invalidation '{code}' must remain readable as stale.");
            }
            transport.State = valid.Clone();
            transport.State.Status = (PcbDrcJobStatus)6;
            transport.State.ResultsFresh = false;
            transport.State.CancellationRequested = true;
            transport.State.ErrorCode = "native_exception";
            transport.State.ErrorMessage = "The worker failed.";
            Assert.IsTrue((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false,
                "A worker failure must not be relabeled as dependency-stale.");
            transport.State = valid.Clone();
            transport.State.CancellationRequested = true;
            Assert.IsTrue((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false,
                "A completed result cannot claim both cancellation and freshness.");
            transport.State = valid.Clone();
            transport.State.SnapshotComplete = false; transport.State.ResultsFresh = false;
            transport.State.InputWarnings.Add("snapshot_incomplete: generated_item_identity: 1 finding names an object that exists only inside this check.");
            Assert.IsFalse((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false, "An incomplete snapshot that says why is a valid state.");
            transport.State = valid.Clone();
            transport.State.Status = (PcbDrcJobStatus)2;
            transport.State.WorkerFinished = false; transport.State.Progress = 0.2;
            transport.State.ResultsFresh = false; transport.State.CancellationRequested = true;
            transport.State.SnapshotComplete = false;
            transport.State.InputWarnings.Add("snapshot_incomplete: finding_identity_pending: the check has not finished; only a finished check has matched each finding to an object of the open board.");
            Assert.IsFalse((await tool.Cancel(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false, "Cancellation acknowledgement may still be running.");
            // The cancelled check ends without findings: nothing is left to match, and its captured snapshot is complete.
            transport.State.Status = (PcbDrcJobStatus)4; transport.State.WorkerFinished = true;
            transport.State.SnapshotComplete = true; transport.State.InputWarnings.Clear();
            Assert.IsFalse((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false);
            transport.State = valid.Clone();
            var schematic = new DocumentLifecycleState
            {
                Document = new() { Type = (DocumentType)1, Project = document.Project.Clone(), SheetPath = new() },
                ProcessEpoch = transport.Session.Epoch,
                NativeIdentity = Guid.NewGuid().ToString("D"),
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 17 },
                StateSha256 = new string('a', 64), Scope = (DocumentLifecycleScope)1,
                ProjectSettingsIncluded = true
            };
            schematic.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") });
            transport.State.CheckedSchematicState = schematic.Clone();
            string schematicJson = SchematicJson.Formatter.Format(schematic);
            var parity = await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default, schematicJson, true);
            Assert.IsFalse(parity.IsError ?? false, parity.ToString());
            var transmittedSchematic = transport.LastStart!.ExpectedSchematicState;
            Assert.AreEqual(schematic, transmittedSchematic);
            Assert.IsTrue(transport.LastStart.AllowDuplicateSheetNames);
            int beforeInvalid = transport.Calls;
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default)).IsError ?? false);
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default, schematicJson)).IsError ?? false);
            var wrongSource = schematic.Clone(); wrongSource.ProcessEpoch = Guid.NewGuid().ToString("D");
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default,
                SchematicJson.Formatter.Format(wrongSource))).IsError ?? false);
            Assert.AreEqual(beforeInvalid, transport.Calls);
            transport.State.CheckedSchematicState.StateSha256 = new string('b', 64);
            Assert.IsTrue((await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, true, revisionJson, transport.Session.Epoch, default, schematicJson)).IsError ?? false,
                "A different checked schematic cannot be accepted as this operation's result.");

            // Candidate metadata is bound to the native epoch and is removed
            // when the native receipt is observed as permanently stale.  A
            // reconnect must never make a prior candidate look replayable.
            transport.State = valid.Clone();
            transport.State.OperationId = Guid.NewGuid().ToString("D");
            transport.State.CandidateDryRun = true;
            transport.State.CandidateItemIds.Add(candidateRequest.Items[0].Unpack<Track>().Id.Value);
            var candidateRefresh = await tool.Start(transport.Session.InstanceId, json, transport.State.OperationId,
                false, false, false, revisionJson, transport.Session.Epoch, default,
                candidateRequestJson: BoardJson.Formatter.Format(candidateRequest));
            Assert.IsFalse(candidateRefresh.IsError ?? false, candidateRefresh.ToString());
            Assert.IsTrue(PcbDrcTools.TryGetCandidateReceipt(valid.JobId, transport.Session.Epoch,
                out _, out _, out _, out _, out _));
            Assert.IsFalse(PcbDrcTools.TryGetCandidateReceipt(valid.JobId, Guid.NewGuid().ToString("D"),
                out _, out _, out _, out _, out _));
            transport.State = valid.Clone();
            transport.State.Status = PcbDrcJobStatus.PdrcjsStale;
            transport.State.CancellationRequested = true;
            transport.State.ResultsFresh = false;
            transport.State.WorkerFinished = true;
            transport.State.SnapshotComplete = true;
            transport.State.ErrorCode = "document_changed";
            transport.State.ErrorMessage = "The live PCB changed while DRC was running.";
            Assert.IsFalse((await tool.Job(transport.Session.InstanceId, json, valid.JobId,
                transport.Session.Epoch, default)).IsError ?? false);
            Assert.IsFalse(PcbDrcTools.TryGetCandidateReceipt(valid.JobId, transport.Session.Epoch,
                out _, out _, out _, out _, out _));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task MarkerInventoryPreservesExclusionsAndRejectsIncompleteOrWrongTargets()
    {
        string directory = Directory.CreateTempSubdirectory("drc-marker-contract-").FullName;
        try
        {
            var transport = new MarkerTransport();
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new PcbDrcTools(registry);
            var document = new DocumentSpecifier { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = directory } };
            var state = new PcbDrcState { Document = document.Clone(), ProcessEpoch = transport.Session.Epoch,
                Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 4 }, MarkerSnapshotComplete = true };
            state.Findings.Add(new PcbDrcFinding { NativeId = Guid.NewGuid().ToString("D"),
                Marker = new() { ErrorType = (Kiapi.Board.DrcErrorType)1 }, Excluded = true, Comment = "explicit fixture exclusion" });
            transport.State = state;
            string json = SchematicJson.Formatter.Format(document);
            var result = await tool.Read(transport.Session.InstanceId, json, default);
            Assert.IsFalse(result.IsError ?? false);
            Assert.AreEqual(state, SchematicJson.Parser.Parse<PcbDrcState>(((TextContentBlock)result.Content.Single()).Text));
            Assert.IsFalse(state.ResultsFreshnessKnown);
            foreach (int variant in Enumerable.Range(0, 7))
            {
                transport.State = state.Clone();
                switch (variant)
                {
                    case 0: transport.State.Document.BoardFilename = "other.kicad_pcb"; break;
                    case 1: transport.State.MarkerSnapshotComplete = false; break;
                    case 2: transport.State.Running = true; break;
                    case 3: transport.State.Findings[0].NativeId = "bad"; break;
                    case 4: transport.State.Findings.Add(transport.State.Findings[0].Clone()); break;
                    case 5: transport.State.Revision.Epoch = "DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD"; break;
                    // A marker inventory observed by another native process is
                    // not a recovery receipt for this attached instance.  The
                    // managed boundary must reject it before exposing markers.
                    case 6: transport.State.ProcessEpoch = Guid.NewGuid().ToString("D"); break;
                }
                var rejected = await tool.Read(transport.Session.InstanceId, json, default);
                Assert.IsTrue(rejected.IsError);
                if (variant == 6)
                    Assert.AreEqual("invalid_drc_state", rejected.StructuredContent!.Value.GetProperty("code").GetString());
            }
            transport.State = state.Clone(); transport.State.Running = true;
            transport.State.MarkerSnapshotComplete = false; transport.State.Findings.Clear();
            Assert.IsFalse((await tool.Read(transport.Session.InstanceId, json, default)).IsError ?? false);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int calls = transport.Calls;
            await Assert.ThrowsAsync<OperationCanceledException>(() => tool.Read(transport.Session.InstanceId, json, cancelled.Token));
            Assert.AreEqual(calls, transport.Calls);
            Assert.IsTrue((await tool.Read(transport.Session.InstanceId, "{", default)).IsError);
            Assert.AreEqual(calls, transport.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task OperationLookupReportsUnknownNativeOutcomeAndValidatesFoundIdentity()
    {
        string directory = Directory.CreateTempSubdirectory("drc-operation-contract-").FullName;
        try
        {
            var transport = new JobTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new PcbDrcTools(registry);
            var document = new DocumentSpecifier { Type = (DocumentType)3, BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = directory } };
            var input = new NativeDocumentInput
            {
                Type = NativeDocumentType.Pcb,
                Project = new NativeProjectInput { Name = "fixture", Path = directory },
                BoardFilename = "fixture.kicad_pcb"
            };
            string operationId = Guid.NewGuid().ToString("D");
            transport.OperationResponse = new NativeCapabilityPcbDrcReadOperationResponse
            {
                Document = document.Clone(), OperationId = operationId, ProcessEpoch = transport.Session.Epoch,
                Found = false
            };

            var missing = await tool.Operation(transport.Session.InstanceId, input, operationId,
                transport.Session.Epoch, default);
            Assert.IsFalse(missing.IsError ?? false,
                missing.StructuredContent?.GetRawText() ?? missing.ToString());
            using var missingDocument = JsonDocument.Parse(((TextContentBlock)missing.Content.Single()).Text);
            var missingJson = missingDocument.RootElement;
            Assert.IsFalse(missingJson.GetProperty("found").GetBoolean());
            Assert.AreEqual("unknown-native-outcome", missingJson.GetProperty("retrySafety").GetString());
            Assert.IsFalse(missingJson.TryGetProperty("job", out _), "found=false must omit a fabricated job state.");
            Assert.AreEqual(document, transport.LastOperation!.Document);
            Assert.AreEqual(operationId, transport.LastOperation.OperationId);
            Assert.AreEqual(transport.Session.Epoch, transport.LastOperation.ProcessEpoch);
            Assert.AreEqual(1, transport.Calls);

            // Malformed or stale caller identities fail before lookup dispatch.
            foreach (var invalid in new (string Operation, string Epoch, string Code)[]
            {
                (Guid.Empty.ToString("D"), transport.Session.Epoch, "invalid_operation_id"),
                ("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", transport.Session.Epoch, "invalid_operation_id"),
                (operationId, Guid.Empty.ToString("D"), "invalid_process_epoch"),
                (operationId, "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB", "invalid_process_epoch"),
                (operationId, Guid.NewGuid().ToString("D"), "stale_process_epoch")
            })
            {
                var rejected = await tool.Operation(transport.Session.InstanceId, input,
                    invalid.Operation, invalid.Epoch, default);
                Assert.IsTrue(rejected.IsError ?? false, rejected.ToString());
                Assert.AreEqual(invalid.Code, rejected.StructuredContent!.Value.GetProperty("code").GetString());
            }
            Assert.AreEqual(1, transport.Calls);

            var job = new PcbDrcJobState
            {
                Document = document.Clone(), JobId = Guid.NewGuid().ToString("D"), OperationId = operationId,
                ProcessEpoch = transport.Session.Epoch,
                CheckedRevision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 8 },
                Status = (PcbDrcJobStatus)3, Progress = 1, ResultsFresh = true,
                WorkerFinished = true, SnapshotComplete = true
            };
            transport.OperationResponse.Found = true;
            transport.OperationResponse.Job = job;
            var found = await tool.Operation(transport.Session.InstanceId, input, operationId,
                transport.Session.Epoch, default);
            Assert.IsFalse(found.IsError ?? false, found.ToString());
            using var foundDocument = JsonDocument.Parse(((TextContentBlock)found.Content.Single()).Text);
            var foundJson = foundDocument.RootElement;
            Assert.IsTrue(foundJson.GetProperty("found").GetBoolean());
            Assert.AreEqual(operationId, foundJson.GetProperty("operationId").GetString());
            Assert.AreEqual(job.JobId, foundJson.GetProperty("job").GetProperty("jobId").GetString());
            Assert.AreEqual(2, transport.Calls);

            var valid = transport.OperationResponse.Clone();
            foreach (var invalid in new (Action<NativeCapabilityPcbDrcReadOperationResponse> Change, string Code)[]
            {
                (response => response.Document = null, "invalid_drc_operation_result"),
                (response => response.Document.BoardFilename = "other.kicad_pcb", "invalid_drc_operation_result"),
                (response => response.OperationId = Guid.NewGuid().ToString("D"), "invalid_drc_operation_result"),
                (response => response.ProcessEpoch = Guid.NewGuid().ToString("D"), "invalid_drc_operation_result"),
                (response => response.Found = false, "invalid_drc_operation_result"),
                (response => response.Job = null, "invalid_drc_operation_result"),
                (response => response.Job.Document.BoardFilename = "other.kicad_pcb", "invalid_drc_job_state"),
                (response => response.Job.ProcessEpoch = Guid.NewGuid().ToString("D"), "invalid_drc_job_state"),
                (response => response.Job.OperationId = Guid.NewGuid().ToString("D"), "invalid_drc_operation_result"),
                (response => response.Job.JobId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", "invalid_drc_job_state"),
                (response => response.Job.CheckedRevision.Epoch = Guid.Empty.ToString("D"), "invalid_drc_job_state"),
                (response => response.Job.WorkerFinished = false, "invalid_drc_job_state"),
                (response => response.Job.SnapshotComplete = false, "invalid_drc_job_state")
            })
            {
                transport.OperationResponse = valid.Clone();
                invalid.Change(transport.OperationResponse);
                var rejected = await tool.Operation(transport.Session.InstanceId, input, operationId,
                    transport.Session.Epoch, default);
                Assert.IsTrue(rejected.IsError ?? false, rejected.ToString());
                Assert.AreEqual(invalid.Code, rejected.StructuredContent!.Value.GetProperty("code").GetString());
            }

            // The lookup uses normal job-state semantics: it can report a running
            // or stale check without promoting it to complete or fresh.
            transport.OperationResponse = valid.Clone();
            transport.OperationResponse.Job.Status = (PcbDrcJobStatus)2;
            transport.OperationResponse.Job.WorkerFinished = false;
            transport.OperationResponse.Job.Progress = 0.2;
            transport.OperationResponse.Job.ResultsFresh = false;
            transport.OperationResponse.Job.SnapshotComplete = false;
            transport.OperationResponse.Job.InputWarnings.Add("snapshot_incomplete: finding_identity_pending: native fixture check is running.");
            Assert.IsFalse((await tool.Operation(transport.Session.InstanceId, input, operationId,
                transport.Session.Epoch, default)).IsError ?? false);
            transport.OperationResponse = valid.Clone();
            transport.OperationResponse.Job.Status = (PcbDrcJobStatus)6;
            transport.OperationResponse.Job.ResultsFresh = false;
            transport.OperationResponse.Job.CancellationRequested = true;
            transport.OperationResponse.Job.ErrorCode = "document_changed";
            transport.OperationResponse.Job.ErrorMessage = "The live PCB changed.";
            Assert.IsFalse((await tool.Operation(transport.Session.InstanceId, input, operationId,
                transport.Session.Epoch, default)).IsError ?? false);
            Assert.IsNull(transport.LastStart, "A read-only lookup must never submit a start or replay request.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task TypedLifecycleInputsPreserveIdentityAndRejectMalformedDocuments()
    {
        string directory = Directory.CreateTempSubdirectory("drc-typed-lifecycle-").FullName;
        try
        {
            var transport = new JobTransport();
            transport.Session.Epoch = Guid.NewGuid().ToString("D");
            transport.Session.ProjectPath = Path.Combine(directory, "fixture.kicad_pro");
            var registry = new InstanceRegistry(transport, directory);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")), transport.Session.InstanceId);
            var tool = new PcbDrcTools(registry);
            var document = new DocumentSpecifier
            {
                Type = DocumentType.DoctypePcb,
                BoardFilename = "fixture.kicad_pcb",
                Project = new() { Name = "fixture", Path = directory }
            };
            var inputDocument = new NativeDocumentInput
            {
                Type = NativeDocumentType.Pcb,
                BoardFilename = "fixture.kicad_pcb",
                Project = new NativeProjectInput { Name = "fixture", Path = directory }
            };
            string jobId = Guid.NewGuid().ToString("D");
            transport.State = new PcbDrcJobState
            {
                Document = document.Clone(),
                JobId = jobId,
                OperationId = Guid.NewGuid().ToString("D"),
                ProcessEpoch = transport.Session.Epoch,
                CheckedRevision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 4 },
                Status = (PcbDrcJobStatus)3,
                Progress = 1,
                WorkerFinished = true,
                SnapshotComplete = true,
                ResultsFresh = true
            };

            var accepted = await tool.JobTyped(transport.Session.InstanceId, new PcbDrcJobInput
            {
                Document = inputDocument,
                JobId = jobId,
                ProcessEpoch = transport.Session.Epoch
            }, default);
            Assert.IsFalse(accepted.IsError ?? false, accepted.StructuredContent?.GetRawText() ?? accepted.ToString());
            Assert.AreEqual(jobId, accepted.StructuredContent!.Value.GetProperty("jobId").GetString());
            Assert.AreEqual(1, transport.Calls);

            foreach (var invalid in new (PcbDrcJobInput Input, string Code)[]
            {
                (new PcbDrcJobInput { JobId = jobId, ProcessEpoch = transport.Session.Epoch }, "invalid_pcb_drc_typed_job"),
                (new PcbDrcJobInput { Document = inputDocument, JobId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", ProcessEpoch = transport.Session.Epoch }, "invalid_pcb_drc_typed_job"),
                (new PcbDrcJobInput { Document = inputDocument, JobId = jobId, ProcessEpoch = Guid.NewGuid().ToString("D") }, "stale_process_epoch"),
                (new PcbDrcJobInput
                {
                    Document = new NativeDocumentInput
                    {
                        Type = NativeDocumentType.Schematic,
                        Project = new NativeProjectInput { Name = "fixture", Path = directory },
                        SheetPath = new NativeSheetPathInput { Path = [new NativeUuidInput { Value = Guid.NewGuid().ToString("D") }] }
                    },
                    JobId = jobId,
                    ProcessEpoch = transport.Session.Epoch
                }, "invalid_pcb_drc_typed_job")
            })
            {
                var rejected = await tool.JobTyped(transport.Session.InstanceId, invalid.Input, default);
                Assert.IsTrue(rejected.IsError ?? false, rejected.ToString());
                JsonElement structured = rejected.StructuredContent!.Value;
                string code = structured.TryGetProperty("errorCode", out JsonElement typedCode)
                    ? typedCode.GetString()! : structured.GetProperty("code").GetString()!;
                Assert.AreEqual(invalid.Code, code);
            }
            Assert.AreEqual(1, transport.Calls,
                "Malformed structured identities and documents must be rejected before native dispatch.");

            transport.OperationResponse = new NativeCapabilityPcbDrcReadOperationResponse
            {
                Document = document.Clone(),
                OperationId = transport.State.OperationId,
                ProcessEpoch = transport.Session.Epoch,
                Found = false
            };
            var operation = await tool.OperationTyped(transport.Session.InstanceId, new PcbDrcOperationInput
            {
                Document = inputDocument,
                OperationId = transport.State.OperationId,
                ProcessEpoch = transport.Session.Epoch
            }, default);
            Assert.IsFalse(operation.IsError ?? false, operation.ToString());
            using var operationJson = JsonDocument.Parse(((TextContentBlock)operation.Content.Single()).Text);
            Assert.IsFalse(operationJson.RootElement.GetProperty("found").GetBoolean());
            Assert.AreEqual(2, transport.Calls);

            var cancel = await tool.CancelTyped(transport.Session.InstanceId, new PcbDrcCancelInput
            {
                Document = inputDocument,
                JobId = jobId,
                ProcessEpoch = transport.Session.Epoch
            }, default);
            Assert.IsFalse(cancel.IsError ?? false, cancel.ToString());
            Assert.AreEqual(jobId, cancel.StructuredContent!.Value.GetProperty("jobId").GetString());
            Assert.AreEqual(3, transport.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MarkerTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public PcbDrcState State { get; set; } = new();
        public int Calls { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!ApiRequest.Parser.ParseFrom(request).Message.Is(ReadPcbDrcState.Descriptor))
                return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            ++Calls;
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(State) }.ToByteArray());
        }
    }

    private sealed class JobTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Session { get; } = new();
        public PcbDrcJobState State { get; set; } = new();
        public PcbDrcJobState? ReplacementState { get; set; }
        public NativeCapabilityPcbDrcReadOperationResponse OperationResponse { get; set; } = new();
        public int Calls { get; private set; }
        public StartPcbDrcJob? LastStart { get; private set; }
        public NativeCapabilityPcbDrcReadOperation? LastOperation { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            if (!message.Is(StartPcbDrcJob.Descriptor) && !message.Is(ReadPcbDrcJob.Descriptor)
                && !message.Is(CancelPcbDrcJob.Descriptor)
                && !message.Is(NativeCapabilityPcbDrcReadOperation.Descriptor)
                && !message.Is(NativeCapabilityPcbDrcReplacementJob.Descriptor))
                return Session.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            ++Calls;
            if (message.Is(StartPcbDrcJob.Descriptor)) LastStart = message.Unpack<StartPcbDrcJob>();
            if (message.Is(NativeCapabilityPcbDrcReadOperation.Descriptor))
                LastOperation = message.Unpack<NativeCapabilityPcbDrcReadOperation>();
            if (message.Is(NativeCapabilityPcbDrcReplacementJob.Descriptor))
            {
                // The native replacement read is the receipt boundary for a
                // detached candidate. A real peer compares the complete Any
                // payload and returns the state with this bit set.
                (ReplacementState ?? State).CandidatePayloadVerified = true;
            }
            var result = message.Is(NativeCapabilityPcbDrcReadOperation.Descriptor)
                ? Any.Pack(OperationResponse)
                : Any.Pack(message.Is(NativeCapabilityPcbDrcReplacementJob.Descriptor)
                    ? (ReplacementState ?? State) : State);
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)1 }, Message = result }.ToByteArray());
        }
    }
}
