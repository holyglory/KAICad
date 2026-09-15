using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyCheckedSchematicBatch(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<DocumentLifecycleState> State() => client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, token);
        Task<CheckedSchematicBatchReceipt> Apply(CheckedSchematicBatch request) =>
            client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        CheckedSchematicBatch Request(DocumentLifecycleState state, string title)
        {
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), ExpectedRevision = state.Revision.Clone(),
                DocumentEpoch = state.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Checked native batch fixture" };
            batch.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = title } });
            return new() { Batch = batch, ExpectedState = state.Clone() };
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var initial = await State();
        Assert.IsFalse(initial.CompleteChangeTracking, "Full event tracking is still a separate qualification.");
        var stale = Request(initial, "Must not overwrite the later edit");
        var initialTitle = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
            { Document = document, TitleBlock = new() { Title = "Independent native edit" } }, token);
        var current = await State();
        var rejected = await Apply(stale);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, rejected.Status);
        Assert.AreEqual("stale_document_state", rejected.ErrorCode);
        Assert.AreEqual(current, await State());

        // Same revision, stale content digest: counter equality is insufficient.
        var sameCursor = Request(current, "Must not trust a cursor alone");
        sameCursor.ExpectedState.StateSha256 = initial.StateSha256;
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, (await Apply(sameCursor)).Status);
        Assert.AreEqual(current, await State());
        Assert.AreEqual(rejected, await Apply(stale), "A rejected identity must not become a new edit later.");

        var broken = Request(current, "Must roll back with the invalid operation");
        broken.Batch.Operations.Add(new SchematicItemOperation());
        var brokenResult = await Apply(broken);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, brokenResult.Status);
        Assert.AreEqual("native_batch_rejected", brokenResult.ErrorCode);
        Assert.AreEqual(current, await State());

        string projectFile = Path.Combine(document.Project.Path, document.Project.Name + ".kicad_pro");
        byte[] originalFile = await File.ReadAllBytesAsync(projectFile, token);
        try
        {
            // Explicit external-writer fault injection in this disposable fixture.
            await File.AppendAllTextAsync(projectFile, "\n ", token);
            var diskChanged = await State();
            Assert.AreEqual(current.StateSha256, diskChanged.StateSha256);
            Assert.IsTrue(diskChanged.FileBaselines.Any(x => x.Status != NativeFileBaselineStatus.NfbsUnchanged));
            var diskResult = await Apply(Request(diskChanged, "Disk conflict must pause"));
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, diskResult.Status);
            Assert.AreEqual("file_baseline_conflict", diskResult.ErrorCode);
            Assert.AreEqual(diskChanged, await State());
        }
        finally { await File.WriteAllBytesAsync(projectFile, originalFile, token); }
        Assert.AreEqual(current, await State());

        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-checked-mcp"),
            Path.Combine(evidence, instanceId + "-checked-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var checkedRequest = Request(current, "Accepted exact-state edit");
        Guid origin = Guid.NewGuid(); checkedRequest.Batch.OriginId = origin.ToString("D");
        var electrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document }, token);
        var design = ProbeElectricalModel(electrical);
        var recovery = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-checked-recovery.json"));
        var recoveryState = new DesignRecoveryState(origin, Guid.Parse(instanceId),
            new(current.Revision.Epoch, current.Revision.Sequence), electrical.Hierarchy.TrackingComplete,
            design, System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [])), electrical.Hierarchy.Data.Clone(), [],
            PendingMutation: checkedRequest.Batch.Clone(), BaselineElectrical: electrical.Clone(), ObservedElectrical: electrical.Clone(),
            PendingNativeState: current.Clone());
        var pending = recovery.Save(recoveryState, null);
        Assert.AreEqual(DesignRecoveryDisposition.NotFound, (await DesignRecoveryInspector.InspectAsync(recovery, client, token)).Disposition);
        string requestJson = SchematicJson.Formatter.Format(checkedRequest);
        var result = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson });
        RequireToolSuccess(result);
        var receipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(result.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status);
        Assert.AreEqual(current, receipt.ObservedBefore);
        Assert.AreEqual(receipt.ObservedAfter, await State());
        var recovered = await DesignRecoveryInspector.InspectAsync(new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-checked-recovery.json")), client, token);
        Assert.AreEqual(DesignRecoveryDisposition.CompletedNeedsReconciliation, recovered.Disposition);
        Assert.AreEqual(receipt, recovered.CheckedReceipt); Assert.IsNull(recovered.Receipt);
        Assert.AreEqual(pending.RevisionToken, recovery.Read()!.RevisionToken);
        Assert.AreEqual(current, recovery.Read()!.State.PendingNativeState);
        Assert.IsTrue(receipt.Result.Revision.Sequence > current.Revision.Sequence);
        Assert.IsFalse(result.GetProperty("structuredContent").GetProperty("designFilesSaved").GetBoolean());
        CollectionAssert.AreEqual(originalFile, await File.ReadAllBytesAsync(projectFile, token));

        var duplicate = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson });
        RequireToolSuccess(duplicate);
        Assert.AreEqual(receipt, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(duplicate.GetProperty("structuredContent").GetProperty("receipt").GetRawText()));
        Assert.AreEqual(receipt.ObservedAfter, await State());
        var inspection = await mcp.Tool("kicad_schematic_checked_batch_receipt", new { instanceId, requestJson });
        RequireToolSuccess(inspection);
        Assert.AreEqual(receipt, SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(inspection.GetProperty("structuredContent").GetProperty("receipt").GetRawText()));
        Assert.IsFalse(inspection.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean());

        var changed = checkedRequest.Clone(); changed.Batch.Description = "Different checked request";
        Assert.IsTrue((await mcp.Tool("kicad_schematic_apply_checked_batch", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(changed) })).GetProperty("isError").GetBoolean());
        Assert.AreEqual(receipt.ObservedAfter, await State());
        var missing = Request(await State(), "Not sent");
        var notFound = await mcp.Tool("kicad_schematic_checked_batch_receipt", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(missing) });
        RequireToolSuccess(notFound);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsNotFound,
            SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(notFound.GetProperty("structuredContent").GetProperty("receipt").GetRawText()).Status);

        foreach (var (key, title) in new[] { ("z", "Independent native edit"), ("y", "Accepted exact-state edit") })
        {
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, limit.Token)).Title != title)
                await Task.Delay(50, limit.Token);
        }
        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        Assert.AreEqual(observation.Preview.Revision, observation.Snapshot.Revision);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-checked-batch.png"), observation.Preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-receipt.json"), SchematicJson.Formatter.Format(receipt), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-request.json"), requestJson, token);
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = initialTitle }, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
    }
}
