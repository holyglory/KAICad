using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeCheckedRevert(NativeClient client, DocumentSpecifier document,
        string evidence, string instanceId, CancellationToken token)
    {
        var original = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
            new() { Document = document }, token);
        var clean = await ObserveLifecycleState(client, document, token);
        if (clean.NativeContentDirty || clean.CleanCheckpointSha256 != clean.StateSha256)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            clean = await ObserveLifecycleState(client, document, token);
        }
        Assert.IsFalse(clean.NativeContentDirty, "Checked revert starts from a clean checkpoint.");

        var edited = original.Clone();
        edited.Title = "checked-revert-" + Guid.NewGuid().ToString("N");
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(
            new() { Document = document, TitleBlock = edited }, token);
        var dirty = await ObserveLifecycleState(client, document, token);
        Assert.IsTrue(dirty.NativeContentDirty, "The native title edit must be unsaved before revert.");

        string operationId = Guid.NewGuid().ToString("D");
        var request = new NativeCapabilityCheckedRevertDocument
        {
            Document = document.Clone(), OperationId = operationId, ExpectedState = dirty.Clone()
        };
        var receipt = await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(request, token);
        Assert.AreEqual(LifecycleOperationStatus.LosSaved, receipt.Status, receipt.ErrorMessage);
        Assert.IsTrue(receipt.RevertCompleted, "The native terminal result must identify a checked revert.");
        Assert.IsFalse(receipt.ObservedState.NativeContentDirty);
        Assert.AreEqual(document, receipt.Document);

        var replay = await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(request, token);
        Assert.AreEqual(receipt, replay, "The native operation UUID must replay its terminal receipt without reloading twice.");
        Assert.AreEqual(original.Title, (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
            new() { Document = document }, token)).Title, "Reload must discard the unsaved native title edit.");

        var stale = dirty.Clone();
        stale.Revision.Sequence++;
        var rejected = await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(new()
        {
            Document = document.Clone(), OperationId = Guid.NewGuid().ToString("D"), ExpectedState = stale
        }, token);
        Assert.AreEqual(LifecycleOperationStatus.LosRejected, rejected.Status);
        Assert.AreEqual("stale_document_state", rejected.ErrorCode);
        Assert.AreEqual(original.Title, (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
            new() { Document = document }, token)).Title, "A stale checked revert must not mutate the document.");

        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-revert.json"),
            SchematicJson.Formatter.Format(receipt), token);
    }

    private static async Task VerifyNativeCheckedPcbRevert(NativeClient client, DocumentSpecifier board,
        string evidence, string instanceId, CancellationToken token)
    {
        var clean = await ObserveLifecycleState(client, board, token);
        if (clean.NativeContentDirty || clean.CleanCheckpointSha256 != clean.StateSha256)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = board }, token);
            clean = await ObserveLifecycleState(client, board, token);
        }
        Assert.IsFalse(clean.NativeContentDirty, "Checked PCB revert starts from a clean checkpoint.");

        async Task<Google.Protobuf.Collections.RepeatedField<Any>> Items() =>
            (await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            {
                Header = new() { Document = board },
                Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
            }, token)).Items;

        var beforeItems = (await Items()).Select(item => Any.Parser.ParseFrom(item.ToByteArray())).ToArray();
        var temporaryTrack = new Track
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 4_000_000, YNm = 4_000_000 },
            End = new() { XNm = 5_000_000, YNm = 4_000_000 },
            Width = new() { ValueNm = 250_000 }, Layer = BoardLayer.BlFCu
        };
        await client.InvokeAsync<CreateItems, CreateItemsResponse>(new()
        {
            Header = new() { Document = board }, Items = { Any.Pack(temporaryTrack) }
        }, token);
        var dirty = await ObserveLifecycleState(client, board, token);
        Assert.IsTrue(dirty.NativeContentDirty, "The temporary PCB track must make the board dirty.");

        string operationId = Guid.NewGuid().ToString("D");
        var request = new NativeCapabilityCheckedRevertDocument
        {
            Document = board.Clone(), OperationId = operationId, ExpectedState = dirty.Clone()
        };
        var receipt = await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(request, token);
        Assert.AreEqual(LifecycleOperationStatus.LosSaved, receipt.Status, receipt.ErrorMessage);
        Assert.IsTrue(receipt.RevertCompleted);
        Assert.IsFalse(receipt.ObservedState.NativeContentDirty);
        Assert.AreEqual(receipt, await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(request, token));

        var afterItems = (await Items()).Select(item => Any.Parser.ParseFrom(item.ToByteArray())).ToArray();
        Assert.AreEqual(beforeItems.Length, afterItems.Length, "Checked PCB revert must restore the original route inventory.");
        for (int i = 0; i < beforeItems.Length; i++)
            CollectionAssert.AreEqual(beforeItems[i].ToByteArray(), afterItems[i].ToByteArray());

        var stale = dirty.Clone();
        stale.Revision.Sequence++;
        var rejected = await client.InvokeAsync<NativeCapabilityCheckedRevertDocument, LifecycleOperationResult>(new()
        {
            Document = board.Clone(), OperationId = Guid.NewGuid().ToString("D"), ExpectedState = stale
        }, token);
        Assert.AreEqual(LifecycleOperationStatus.LosRejected, rejected.Status);
        Assert.AreEqual("stale_document_state", rejected.ErrorCode);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-checked-pcb-revert.json"),
            SchematicJson.Formatter.Format(receipt), token);
    }
}
