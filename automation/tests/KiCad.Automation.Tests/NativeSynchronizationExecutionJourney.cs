using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySynchronizationExecution(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        string designPath = Path.Combine(evidence, instanceId + "-sync-design.xml");
        string recordPath = Path.Combine(evidence, instanceId + "-sync-recovery.json");
        var store = new DesignRecoveryStore(recordPath);
        async Task<CheckedSchematicState> Capture() => await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        var initial = await Capture(); var model = ProbeElectricalModel(initial.Electrical);
        byte[] original = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(model, []));
        await File.WriteAllBytesAsync(designPath, original, token);
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId),
            new(initial.State.Revision.Epoch, initial.State.Revision.Sequence), initial.Electrical.Hierarchy.TrackingComplete,
            model, original, initial.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone()), null);

        Console.WriteLine($"Native synchronization {instanceId}: establish a known synchronized baseline");
        var initialized = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsTrue(initialized.SynchronizationCommitted); Assert.IsFalse(initialized.NativeMutationCommitted);
        saved = store.Read()!;
        Console.WriteLine($"Native synchronization {instanceId}: unchanged reapplication");
        var noOp = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsTrue(noOp.SynchronizationCommitted); Assert.IsFalse(noOp.NativeMutationCommitted);
        Assert.AreEqual(saved.RevisionToken, noOp.RecoveryRevisionToken);

        Console.WriteLine($"Native synchronization {instanceId}: native edit to XML");
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
            { Document = document.Clone(), TitleBlock = new() { Title = "Native edit synchronized to XML" } }, token);
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, saved.RevisionToken, token, includeElectrical: true);
        Guid reverseOperationId = Guid.NewGuid(); string reverseRequestToken = saved.RevisionToken;
        var reverse = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, reverseOperationId, token);
        Assert.IsTrue(reverse.SynchronizationCommitted); Assert.IsFalse(reverse.NativeMutationCommitted);
        Assert.IsTrue(reverse.NativeFilesSaved);
        Assert.IsNotNull(reverse.PreviousXmlPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(reverse.PreviousXmlPath, token));
        await RequireAgreement("Native edit synchronized to XML");

        foreach (var (descriptor, title) in new[]
        {
            (CheckedSchematicBatch.Descriptor, "Recovered lost edit reply"),
            (CheckedSaveDocument.Descriptor, "Recovered lost save reply")
        })
        {
            Console.WriteLine($"Native synchronization {instanceId}: {title}");
            saved = store.Read()!;
            var desired = saved.State.Baseline with { Schematic = saved.State.Baseline.Schematic.Clone() };
            desired.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.TitleBlock.Title = title;
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
            await File.WriteAllBytesAsync(designPath, bytes, token);
            saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
            var drop = new LostSynchronizationReply(descriptor);
            Guid operationId = Guid.NewGuid();
            var interruptedClient = new NativeClient(drop, client.Endpoint, client.Epoch);
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, interruptedClient, designPath, saved.RevisionToken, operationId, token));
            Assert.IsTrue(drop.Dropped, "The failure must occur only after the real KiCad response was received.");
            var pending = new DesignRecoveryStore(recordPath).Read()!;
            Assert.IsNotNull(pending.State.PendingPublication); Assert.IsNotNull(pending.State.PendingMutation);
            string nativeOperation = pending.State.PendingMutation.OperationId;
            Guid publicationOperation = pending.State.PendingPublication.OperationId;
            await File.WriteAllBytesAsync(designPath, "<newer-unobserved-xml/>"u8.ToArray(), token);
            var auditing = new SynchronizationRequestAudit();
            var auditClient = new NativeClient(auditing, client.Endpoint, client.Epoch);
            var changedXml = await Assert.ThrowsExactlyAsync<KiCad.Automation.Model.AutomationException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, auditClient, designPath, saved.RevisionToken, operationId, token));
            Assert.AreEqual("publication_target_changed", changedXml.Code);
            Assert.AreEqual(0, auditing.Mutations, "File-intake lag cannot authorize replaying a native mutation against newer XML.");
            Assert.AreEqual(pending.RevisionToken, store.Read()!.RevisionToken);
            await File.WriteAllBytesAsync(designPath, bytes, token);
            var wrongPath = await Assert.ThrowsExactlyAsync<KiCad.Automation.Model.AutomationException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath + ".other.xml", saved.RevisionToken, operationId, token));
            Assert.AreEqual("publication_target_mismatch", wrongPath.Code);
            var completed = await SchematicSynchronizationExecutor.ApplyAsync(new(recordPath), client, designPath, saved.RevisionToken, operationId, token);
            Assert.IsTrue(completed.SynchronizationCommitted); Assert.IsTrue(completed.NativeMutationCommitted);
            Assert.AreEqual(nativeOperation, completed.NativeReceipt!.OperationId);
            Assert.AreEqual(publicationOperation, completed.PublicationId);
            Assert.IsFalse(store.Read()!.State.HasPendingWork);
            await RequireAgreement(title);
            var replay = await SchematicSynchronizationExecutor.ApplyAsync(new(recordPath), client, designPath, saved.RevisionToken, operationId, token);
            Assert.IsTrue(replay.Replayed); Assert.AreEqual(completed.NativeRevision, replay.NativeRevision);
            Assert.AreEqual(completed.NativeReceipt, replay.NativeReceipt);

            byte[] recoveryBefore = await File.ReadAllBytesAsync(recordPath, token);
            DateTime xmlTime = File.GetLastWriteTimeUtc(designPath);
            var repeated = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, completed.RecoveryRevisionToken, Guid.NewGuid(), token);
            Assert.IsFalse(repeated.NativeMutationCommitted); Assert.IsFalse(repeated.NativeFilesSaved);
            Assert.AreEqual(completed.RecoveryRevisionToken, repeated.RecoveryRevisionToken);
            CollectionAssert.AreEqual(recoveryBefore, await File.ReadAllBytesAsync(recordPath, token));
            Assert.AreEqual(xmlTime, File.GetLastWriteTimeUtc(designPath));
        }

        Console.WriteLine($"Native synchronization {instanceId}: rendered keyboard undo to XML");
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, timeout.Token)).Title
                != "Recovered lost edit reply") await Task.Delay(50, timeout.Token);
        }
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
        var undo = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsFalse(undo.NativeMutationCommitted); await RequireAgreement("Recovered lost edit reply");
        byte[] beforeHistoricalReplay = await File.ReadAllBytesAsync(recordPath, token);
        var historicalAudit = new SynchronizationRequestAudit();
        var historical = await SchematicSynchronizationExecutor.ApplyAsync(store,
            new NativeClient(historicalAudit, client.Endpoint, client.Epoch), designPath, reverseRequestToken, reverseOperationId, token);
        Assert.IsTrue(historical.Replayed); Assert.AreEqual(reverse.NativeRevision, historical.NativeRevision);
        Assert.AreEqual(0, historicalAudit.Requests, "Historical completion lookup must not contact or edit KiCad.");
        CollectionAssert.AreEqual(beforeHistoricalReplay, await File.ReadAllBytesAsync(recordPath, token));
        await VerifySynchronizationServiceRestart(client, document, store, designPath, evidence, instanceId, token);
        await VerifySynchronizationProperties(client, document, store, designPath, processId, display, evidence, instanceId, token);
        var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        Assert.AreEqual(image.Snapshot.Revision, image.Preview.Revision);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-sync-execution.png"), image.Preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-sync-execution-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeProjects = 1, xmlAndNativeAgreement = true, lostEditReplyRecovered = true,
            lostSaveReplyRecovered = true, keyboardUndoSynchronized = true, unchangedReapplication = true,
            historicalReceiptReplayed = true, unobservedXmlChangeBlockedNativeReplay = true,
            highLevelMcpQualified = false, automaticEventLoopQualified = false, crossPlatformReady = false
        }), token);

        async Task RequireAgreement(string title)
        {
            var actual = await Capture();
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.AreEqual(title, xml.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.TitleBlock.Title);
            Assert.AreEqual(0, SchematicHierarchyDelta.Plan(actual.Electrical.Hierarchy.Data, xml.Schematic, token).Count);
            var connectivity = SchematicElectricalComparison.Compare(xml, actual.Electrical, [], token);
            Assert.IsTrue(connectivity.PinBindingsComplete); Assert.IsTrue(connectivity.ConnectivityEquivalent);
        }
    }

    private sealed class LostSynchronizationReply(MessageDescriptor descriptor) : INativeTransport
    {
        private readonly NngTransport transport = new();
        internal bool Dropped { get; private set; }
        public async Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            byte[] response = await transport.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            if (!Dropped && ApiRequest.Parser.ParseFrom(request).Message.Is(descriptor))
            {
                Dropped = true;
                throw new IOException("Injected reply loss after the real native response");
            }
            return response;
        }
    }

    private sealed class SynchronizationRequestAudit : INativeTransport
    {
        private readonly NngTransport transport = new();
        internal int Requests { get; private set; }
        internal int Mutations { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Requests++;
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            if (message.Is(CheckedSchematicBatch.Descriptor) || message.Is(CheckedSaveDocument.Descriptor)) Mutations++;
            return transport.ExchangeAsync(endpoint, request, timeout, cancellationToken);
        }
    }
}
