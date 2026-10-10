using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CheckedPcbItemContractTests
{
    [TestMethod]
    public void MixedTransactionRoundTripsAndRequiresEveryExactOperationResult()
    {
        var (request, createItem, updateItem, deleteId, before) = Request();
        var parsed = SchematicJson.Parser.Parse<CheckedPcbItemTransaction>(
            SchematicJson.Formatter.Format(request));
        Assert.AreEqual(request, parsed);
        Assert.AreEqual(createItem, parsed.Operations[0].Create.Items.Single().Unpack<Track>());

        var after = before.Clone();
        after.Revision.Sequence++;
        after.StateSha256 = new string('b', 64);
        var receipt = new CheckedPcbItemTransactionReceipt
        {
            Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
            OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsCompleted,
            RequestVerified = true, Phase = CheckedPcbItemTransactionPhase.CpbtpComplete,
            RollbackState = CheckedPcbItemRollbackState.CpbrsNotAttempted,
            PostCommitState = CheckedPcbItemPostCommitState.CpbpsConfirmed,
            ObservedBefore = before, ObservedAfter = after
        };
        receipt.Results.Add(new PcbItemOperationResult
        {
            Create = new CreateItemsResponse
            {
                Status = ItemRequestStatus.IrsOk,
                CreatedItems = { new ItemCreationResult { Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(createItem) } }
            }
        });
        receipt.Results.Add(new PcbItemOperationResult
        {
            Update = new UpdateItemsResponse
            {
                Status = ItemRequestStatus.IrsOk,
                UpdatedItems = { new ItemUpdateResult { Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(updateItem) } }
            }
        });
        receipt.Results.Add(new PcbItemOperationResult
        {
            Delete = new DeleteItemsResponse
            {
                Status = ItemRequestStatus.IrsOk,
                DeletedItems = { new ItemDeletionResult { Id = deleteId.Clone(), Status = ItemDeletionStatus.IdsOk } }
            }
        });

        CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true);

        var unchangedAfter = receipt.Clone();
        unchangedAfter.ObservedAfter = before.Clone();
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateResult(request, unchangedAfter, inspect: true));

        var missing = receipt.Clone();
        missing.Results.RemoveAt(2);
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, missing, inspect: true));

        var wrongIdentity = receipt.Clone();
        wrongIdentity.Results[2].Delete.DeletedItems[0].Id = new() { Value = Guid.NewGuid().ToString("D") };
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, wrongIdentity, inspect: true));
    }

    [TestMethod]
    public void RolledBackReceiptRequiresConfirmedRollbackAndBeforeStateEquality()
    {
        var (request, _, _, _, before) = Request();
        var receipt = new CheckedPcbItemTransactionReceipt
        {
            Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
            OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsRolledBack,
            RequestVerified = true, Phase = CheckedPcbItemTransactionPhase.CpbtpRollbackConfirmed,
            RollbackState = CheckedPcbItemRollbackState.CpbrsConfirmed,
            PostCommitState = CheckedPcbItemPostCommitState.CpbpsNotAttempted,
            ErrorCode = "update_rejected", ErrorMessage = "rejected", ObservedBefore = before,
            ObservedRollback = before.Clone()
        };
        CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true);

        var missingMessage = receipt.Clone();
        missingMessage.ErrorMessage = "";
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateResult(request, missingMessage, inspect: true));

        var forgedIdentity = receipt.Clone();
        forgedIdentity.ObservedRollback.NativeIdentity = Guid.NewGuid().ToString("D");
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, forgedIdentity, inspect: true));

        var wrongEpoch = receipt.Clone();
        wrongEpoch.ObservedRollback.Revision.Epoch = Guid.NewGuid().ToString("D");
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, wrongEpoch, inspect: true));

        var changedFile = receipt.Clone();
        changedFile.ObservedRollback.FileBaselines[0].CurrentBytes++;
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, changedFile, inspect: true));

        var rejected = receipt.Clone();
        rejected.State = CheckedPcbItemTransactionState.CpbtsRejected;
        rejected.Phase = CheckedPcbItemTransactionPhase.CpbtpAdmissionVerified;
        rejected.RollbackState = CheckedPcbItemRollbackState.CpbrsNotAttempted;
        rejected.ObservedRollback = null;
        rejected.ObservedAfter = before.Clone();
        rejected.ObservedAfter.Revision.Sequence++;
        rejected.ErrorCode = "commit_begin_failed";
        CheckedPcbItemContract.ValidateResult(request, rejected, inspect: true);

        var indeterminate = receipt.Clone();
        indeterminate.State = CheckedPcbItemTransactionState.CpbtsIndeterminate;
        indeterminate.Phase = CheckedPcbItemTransactionPhase.CpbtpRollbackRequested;
        indeterminate.RollbackState = CheckedPcbItemRollbackState.CpbrsUnconfirmed;
        indeterminate.Results.Clear();
        indeterminate.Results.Add(new PcbItemOperationResult());
        indeterminate.ObservedRollback.StateSha256 = new string('c', 64);
        indeterminate.ErrorCode = "rollback_unconfirmed";
        CheckedPcbItemContract.ValidateResult(request, indeterminate, inspect: true);

        receipt.RollbackState = CheckedPcbItemRollbackState.CpbrsUnconfirmed;
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true));
    }

    [TestMethod]
    public void MissingReceiptIsInspectionOnly()
    {
        var (request, _, _, _, _) = Request();
        var receipt = new CheckedPcbItemTransactionReceipt
        {
            Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
            OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsNotFound
        };
        CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true);
        Assert.ThrowsExactly<AutomationException>(() => CheckedPcbItemContract.ValidateResult(request, receipt, inspect: false));
    }

    [TestMethod]
    public void CheckedItemAdmissionRequiresSupportedCanonicalCreateAndUpdateIdentities()
    {
        var (request, create, _, _, _) = Request();

        var missingIdentity = request.Clone();
        var missing = create.Clone();
        missing.Id = null;
        missingIdentity.Operations[0].Create.Items[0] = Any.Pack(missing);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateRequest(missingIdentity, request.ProcessEpoch, inspect: true));

        var uppercaseIdentity = request.Clone();
        var uppercase = create.Clone();
        uppercase.Id.Value = uppercase.Id.Value.ToUpperInvariant();
        uppercaseIdentity.Operations[0].Create.Items[0] = Any.Pack(uppercase);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateRequest(uppercaseIdentity, request.ProcessEpoch, inspect: true));

        var unsupported = request.Clone();
        unsupported.Operations[0].Create.Items[0] = new Any
        {
            TypeUrl = "type.googleapis.com/kiapi.board.types.FutureItem",
            Value = Google.Protobuf.ByteString.CopyFromUtf8("future")
        };
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateRequest(unsupported, request.ProcessEpoch, inspect: true));
    }

    [TestMethod]
    public void GroupDeleteReceiptRequiresEveryExplicitMemberIdentity()
    {
        var (baseRequest, _, _, _, before) = Request();
        string groupId = Guid.NewGuid().ToString("D");
        string memberId = Guid.NewGuid().ToString("D");
        var request = baseRequest.Clone();
        request.Operations.Clear();
        request.Operations.Add(new PcbItemOperation
        {
            Delete = new DeleteItems
            {
                Header = new() { Document = request.ExpectedState.Document.Clone() },
                ItemIds = { new KIID { Value = groupId }, new KIID { Value = memberId } }
            }
        });
        CheckedPcbItemContract.ValidateRequest(request, request.ProcessEpoch, inspect: true);

        var after = before.Clone();
        after.Revision.Sequence++;
        after.StateSha256 = new string('b', 64);
        var receipt = new CheckedPcbItemTransactionReceipt
        {
            Document = request.ExpectedState.Document.Clone(), ProcessEpoch = request.ProcessEpoch,
            OperationId = request.OperationId, State = CheckedPcbItemTransactionState.CpbtsCompleted,
            RequestVerified = true, Phase = CheckedPcbItemTransactionPhase.CpbtpComplete,
            RollbackState = CheckedPcbItemRollbackState.CpbrsNotAttempted,
            PostCommitState = CheckedPcbItemPostCommitState.CpbpsConfirmed,
            ObservedBefore = before, ObservedAfter = after
        };
        var deleted = new DeleteItemsResponse { Status = ItemRequestStatus.IrsOk };
        deleted.DeletedItems.Add(new ItemDeletionResult { Id = new() { Value = groupId }, Status = ItemDeletionStatus.IdsOk });
        deleted.DeletedItems.Add(new ItemDeletionResult { Id = new() { Value = memberId }, Status = ItemDeletionStatus.IdsOk });
        receipt.Results.Add(new PcbItemOperationResult { Delete = deleted });
        CheckedPcbItemContract.ValidateResult(request, receipt, inspect: true);

        var missingMember = receipt.Clone();
        missingMember.Results[0].Delete.DeletedItems.RemoveAt(1);
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateResult(request, missingMember, inspect: true));

        var duplicateMember = receipt.Clone();
        duplicateMember.Results[0].Delete.DeletedItems[1].Id = new() { Value = groupId };
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateResult(request, duplicateMember, inspect: true));

        var duplicateRequestMember = request.Clone();
        duplicateRequestMember.Operations[0].Delete.ItemIds.Add(new KIID { Value = memberId });
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedPcbItemContract.ValidateRequest(duplicateRequestMember, duplicateRequestMember.ProcessEpoch, inspect: true));
    }

    private static (CheckedPcbItemTransaction Request, Track Create, Track Update, KIID Delete,
        DocumentLifecycleState State) Request()
    {
        string root = Path.Combine(Path.GetTempPath(), "kicad-pcb-item-transaction-contract");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb, BoardFilename = "board.kicad_pcb",
            Project = new() { Name = "board", Path = root }
        };
        string epoch = Guid.NewGuid().ToString("D");
        string identity = Guid.NewGuid().ToString("D");
        var state = new DocumentLifecycleState
        {
            Document = document.Clone(), ProcessEpoch = epoch, NativeIdentity = identity,
            Scope = DocumentLifecycleScope.DlsPcb, ProjectSettingsIncluded = true,
            DiskBaselineChecked = true, StateSha256 = new string('a', 64),
            Revision = new() { Epoch = identity, Sequence = 5 }
        };
        foreach (string path in new[] { Path.Combine(root, "board.kicad_pcb"), Path.Combine(root, "board.kicad_pro") })
        {
            state.NativeFiles.Add(path);
            state.FileBaselines.Add(new NativeFileBaselineState
            {
                Path = path, BaselinePath = path, BaselineKnown = true, CurrentKnown = true,
                Status = NativeFileBaselineStatus.NfbsUnchanged, BaselineExists = false, CurrentExists = false
            });
        }

        var create = new Track
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") }, Start = new() { XNm = 1_000, YNm = 1_000 },
            End = new() { XNm = 2_000, YNm = 1_000 }, Width = new() { ValueNm = 200 }, Layer = BoardLayer.BlFCu
        };
        var update = create.Clone(); update.Id = new() { Value = Guid.NewGuid().ToString("D") }; update.End.XNm = 3_000;
        var delete = new KIID { Value = Guid.NewGuid().ToString("D") };
        var header = new ItemHeader { Document = document.Clone() };
        var request = new CheckedPcbItemTransaction
        {
            ExpectedState = state, ProcessEpoch = epoch, OperationId = Guid.NewGuid().ToString("D"),
            MaximumResultBytes = 128 * 1024
        };
        request.Operations.Add(new PcbItemOperation { Create = new CreateItems { Header = header.Clone(), Items = { Any.Pack(create) } } });
        request.Operations.Add(new PcbItemOperation { Update = new UpdateItems { Header = header.Clone(), Items = { Any.Pack(update) } } });
        request.Operations.Add(new PcbItemOperation { Delete = new DeleteItems { Header = header.Clone(), ItemIds = { delete.Clone() } } });
        return (request, create, update, delete, state);
    }
}
