using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>
/// Pure validation for the checked general PCB item transaction boundary.
/// Native admission repeats these checks; this boundary gives MCP callers a
/// deterministic error before an IPC mutation and validates retained receipts
/// after a reply or a lost transport response.
/// </summary>
public static class CheckedPcbItemContract
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumResultBytes = 4 * 1024 * 1024;
    private const int MinimumResultBytes = 64 * 1024;

    public static void ValidateRequest(CheckedPcbItemTransaction request, string processEpoch,
        bool inspect = false)
    {
        if (request is null || request.ExpectedState is null || request.Operations.Count == 0)
            throw Invalid("A checked PCB item transaction requires operations and a complete expected state.");

        var expected = request.ExpectedState;
        if (!Uuid(processEpoch) || request.ProcessEpoch != processEpoch || !Uuid(request.OperationId))
            throw Invalid("The checked PCB item transaction must identify one operation and native process epoch.");

        if (expected.Scope != DocumentLifecycleScope.DlsPcb || expected.Document is null
            || expected.Document.Type != DocumentType.DoctypePcb
            || string.IsNullOrWhiteSpace(expected.Document.BoardFilename)
            || expected.Document.Project is null || string.IsNullOrWhiteSpace(expected.Document.Project.Name)
            || string.IsNullOrWhiteSpace(expected.Document.Project.Path)
            || !expected.ProjectSettingsIncluded || expected.Revision is null
            || !Uuid(expected.NativeIdentity) || expected.Revision.Epoch != expected.NativeIdentity
            || !Digest(expected.StateSha256) || expected.ProcessEpoch != processEpoch)
            throw Invalid("The checked PCB item transaction must match one exact PCB lifecycle observation.");

        if (!inspect && !CheckedSchematicContract.FileCoverage(expected))
            throw Invalid("The checked PCB state lacks complete unchanged file-baseline coverage.");

        var createIds = new HashSet<string>(StringComparer.Ordinal);
        var updateIds = new HashSet<string>(StringComparer.Ordinal);
        var deleteIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in request.Operations)
        {
            if (operation is null || operation.ActionCase == PcbItemOperation.ActionOneofCase.None)
                throw Invalid("Every checked PCB item operation must select create, update or delete.");

            switch (operation.ActionCase)
            {
                case PcbItemOperation.ActionOneofCase.Create:
                    ValidateHeader(operation.Create.Header, expected.Document);
                    if (operation.Create.Items.Count == 0)
                        throw Invalid("A checked PCB create operation must contain at least one item.");
                    ValidateIdentities(operation.Create.Items, createIds, updateIds, deleteIds);
                    break;
                case PcbItemOperation.ActionOneofCase.Update:
                    ValidateHeader(operation.Update.Header, expected.Document);
                    if (operation.Update.Items.Count == 0)
                        throw Invalid("A checked PCB update operation must contain at least one item.");
                    ValidateIdentities(operation.Update.Items, updateIds, createIds, deleteIds);
                    break;
                case PcbItemOperation.ActionOneofCase.Delete:
                    ValidateHeader(operation.Delete.Header, expected.Document);
                    if (operation.Delete.ItemIds.Count == 0 || operation.Delete.ItemIds.Any(id => !Kiid(id)))
                        throw Invalid("A checked PCB delete operation must identify canonical item UUIDs.");
                    foreach (var id in operation.Delete.ItemIds)
                        if (!deleteIds.Add(id.Value) || createIds.Contains(id.Value) || updateIds.Contains(id.Value))
                            throw Invalid("Checked PCB create, update and delete operations must use distinct item identities.");
                    break;
                default:
                    throw Invalid("The checked PCB item operation kind is unsupported.");
            }
        }

        if (request.MaximumResultBytes != 0
            && (request.MaximumResultBytes < MinimumResultBytes || request.MaximumResultBytes > MaximumResultBytes))
            throw Invalid("The checked PCB item receipt allowance must be zero or between 64 KiB and 4 MiB.");
        if (request.CalculateSize() > MaximumRequestBytes)
            throw Invalid("The checked PCB item transaction exceeds its bounded 4 MiB request allowance.");
    }

    public static void ValidateResult(CheckedPcbItemTransaction request,
        CheckedPcbItemTransactionReceipt result, bool inspect)
    {
        if (request is null || result is null)
            throw Invalid("A checked PCB item request and receipt are both required.");

        ValidateRequest(request, request.ProcessEpoch, inspect: true);

        if (result.Document is null || !result.Document.Equals(request.ExpectedState!.Document)
            || result.ProcessEpoch != request.ProcessEpoch || result.OperationId != request.OperationId
            || !System.Enum.IsDefined(result.State) || result.State == CheckedPcbItemTransactionState.CpbtsUnknown)
            throw Invalid("The native checked PCB item receipt does not identify the exact request.");

        if (result.State == CheckedPcbItemTransactionState.CpbtsNotFound)
        {
            if (!inspect || result.RequestVerified || result.Replayed || result.Results.Count != 0
                || result.ObservedBefore is not null || result.ObservedRollback is not null
                || result.ObservedAfter is not null || result.ErrorCode.Length != 0
                || result.ErrorMessage.Length != 0)
                throw Invalid("A missing PCB item receipt is read-only and cannot claim a verified result.");
            return;
        }

        if (!result.RequestVerified || string.IsNullOrWhiteSpace(result.ErrorCode)
            && result.State != CheckedPcbItemTransactionState.CpbtsCompleted)
            throw Invalid("A native PCB item receipt must verify the request and explain failed outcomes.");

        if (result.ObservedBefore is not null && !result.ObservedBefore.Equals(request.ExpectedState))
            throw Invalid("The native PCB item receipt contains a before-state for another revision.");

        ValidateResults(request, result);

        switch (result.State)
        {
            case CheckedPcbItemTransactionState.CpbtsCompleted:
                ValidateCompleted(request, result);
                break;
            case CheckedPcbItemTransactionState.CpbtsRolledBack:
                ValidateRolledBack(request, result);
                break;
            case CheckedPcbItemTransactionState.CpbtsRejected:
                ValidateRejected(request, result);
                break;
            case CheckedPcbItemTransactionState.CpbtsIndeterminate:
                ValidateIndeterminate(request, result);
                break;
            default:
                throw Invalid("The native checked PCB item receipt has an unknown terminal state.");
        }
    }

    private static void ValidateCompleted(CheckedPcbItemTransaction request,
        CheckedPcbItemTransactionReceipt result)
    {
        if (result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0
            || result.Results.Count != request.Operations.Count
            || result.Phase != CheckedPcbItemTransactionPhase.CpbtpComplete
            || result.RollbackState != CheckedPcbItemRollbackState.CpbrsNotAttempted
            || result.PostCommitState != CheckedPcbItemPostCommitState.CpbpsConfirmed
            || result.ObservedBefore is null || result.ObservedAfter is null
            || result.ObservedRollback is not null)
            throw Invalid("A completed checked PCB item transaction must contain every exact operation result and post-commit receipt.");

        if (!StateAfter(result.ObservedAfter, request.ExpectedState))
            throw Invalid("A completed checked PCB item transaction lacks a consistent post-commit lifecycle state.");
        if (result.ObservedAfter.Revision!.Sequence < request.ExpectedState.Revision!.Sequence)
            throw Invalid("A completed checked PCB item transaction cannot move the native revision backwards.");
    }

    private static void ValidateRolledBack(CheckedPcbItemTransaction request,
        CheckedPcbItemTransactionReceipt result)
    {
        if (result.ErrorCode.Length == 0 || result.Results.Count > request.Operations.Count
            || result.Phase != CheckedPcbItemTransactionPhase.CpbtpRollbackConfirmed
            || result.RollbackState != CheckedPcbItemRollbackState.CpbrsConfirmed
            || result.PostCommitState != CheckedPcbItemPostCommitState.CpbpsNotAttempted
            || result.ObservedBefore is null || result.ObservedRollback is null
            || !result.ObservedRollback.Equals(result.ObservedBefore)
            || result.ObservedAfter is not null)
            throw Invalid("A rolled-back checked PCB item transaction must prove that native state returned to its before checkpoint.");
    }

    private static void ValidateRejected(CheckedPcbItemTransaction request,
        CheckedPcbItemTransactionReceipt result)
    {
        if (result.ErrorCode.Length == 0 || result.Results.Count != 0
            || result.ObservedAfter is not null || result.ObservedRollback is not null
            || result.RollbackState != CheckedPcbItemRollbackState.CpbrsNotAttempted)
            throw Invalid("A rejected checked PCB item transaction cannot claim staged or committed native changes.");
    }

    private static void ValidateIndeterminate(CheckedPcbItemTransaction request,
        CheckedPcbItemTransactionReceipt result)
    {
        if (result.ErrorCode.Length == 0 || result.Results.Count > request.Operations.Count)
            throw Invalid("An indeterminate checked PCB item transaction must retain partial evidence and explain uncertainty.");
        if (result.ObservedAfter is not null && !StateAfter(result.ObservedAfter, request.ExpectedState))
            throw Invalid("The indeterminate PCB item receipt contains an after-state for another document or process.");
        if (result.ObservedRollback is not null && result.ObservedBefore is not null
            && !result.ObservedRollback.Equals(result.ObservedBefore))
            throw Invalid("The indeterminate PCB item receipt contains a rollback state that differs from its before-state.");
    }

    private static void ValidateResults(CheckedPcbItemTransaction request,
        CheckedPcbItemTransactionReceipt result)
    {
        if (result.Results.Count > request.Operations.Count)
            throw Invalid("The native PCB item receipt contains more results than requested operations.");

        for (int index = 0; index < result.Results.Count; index++)
        {
            var operation = request.Operations[index];
            var operationResult = result.Results[index];

            switch (operation.ActionCase)
            {
                case PcbItemOperation.ActionOneofCase.Create:
                    if (operationResult.ResultCase != PcbItemOperationResult.ResultOneofCase.Create
                        || operationResult.Create.Status != ItemRequestStatus.IrsOk
                        || operationResult.Create.CreatedItems.Count != operation.Create.Items.Count
                        || operationResult.Create.CreatedItems.Select((item, index) => (item, index)).Any(pair => pair.item.Status?.Code != ItemStatusCode.IscOk
                            || pair.item.Item is null || !ItemEquivalent(operation.Create.Items[pair.index], pair.item.Item)))
                        throw Invalid("The native PCB item receipt does not preserve the exact create response.");
                    break;
                case PcbItemOperation.ActionOneofCase.Update:
                    if (operationResult.ResultCase != PcbItemOperationResult.ResultOneofCase.Update
                        || operationResult.Update.Status != ItemRequestStatus.IrsOk
                        || operationResult.Update.UpdatedItems.Count != operation.Update.Items.Count
                        || operationResult.Update.UpdatedItems.Select((item, index) => (item, index)).Any(pair => pair.item.Status?.Code != ItemStatusCode.IscOk
                            || pair.item.Item is null || !ItemEquivalent(operation.Update.Items[pair.index], pair.item.Item)))
                        throw Invalid("The native PCB item receipt does not preserve the exact update response.");
                    break;
                case PcbItemOperation.ActionOneofCase.Delete:
                    if (operationResult.ResultCase != PcbItemOperationResult.ResultOneofCase.Delete
                        || operationResult.Delete.Status != ItemRequestStatus.IrsOk
                        || operationResult.Delete.DeletedItems.Count != operation.Delete.ItemIds.Count
                        || operationResult.Delete.DeletedItems.Any(item => item.Status != ItemDeletionStatus.IdsOk
                            || item.Id is null || !operation.Delete.ItemIds.Any(id => id.Equals(item.Id))))
                        throw Invalid("The native PCB item receipt does not preserve the exact delete response.");
                    break;
                default:
                    throw Invalid("The native PCB item request contains an unsupported operation kind.");
            }
        }
    }

    private static bool StateAfter(DocumentLifecycleState state, DocumentLifecycleState expected)
    {
        return state.Document is not null && state.Document.Equals(expected.Document)
            && state.ProcessEpoch == expected.ProcessEpoch && state.Scope == DocumentLifecycleScope.DlsPcb
            && state.ProjectSettingsIncluded && Uuid(state.NativeIdentity)
            && state.Revision is not null && state.Revision.Epoch == expected.Revision?.Epoch
            && Digest(state.StateSha256) && CheckedSchematicContract.FileCoverage(state);
    }

    private static void ValidateHeader(ItemHeader? header, DocumentSpecifier document)
    {
        if (header?.Document is null || !header.Document.Equals(document))
            throw Invalid("Every checked PCB item operation must target the same explicit board document.");
    }

    private static void ValidateIdentities(IEnumerable<Any> items, HashSet<string> local,
        HashSet<string> otherA, HashSet<string> otherB)
    {
        foreach (var item in items)
        {
            var id = ItemIdentity(item);
            if (id is null) continue;
            if (!local.Add(id) || otherA.Contains(id) || otherB.Contains(id))
                throw Invalid("Checked PCB create, update and delete operations must use distinct item identities.");
        }
    }

    private static bool ItemEquivalent(Any expected, Any actual)
    {
        if (!string.Equals(expected.TypeUrl, actual.TypeUrl, StringComparison.Ordinal)) return false;
        var expectedId = ItemIdentity(expected);
        var actualId = ItemIdentity(actual);
        return expectedId is null || string.Equals(expectedId, actualId, StringComparison.Ordinal);
    }

    private static string? ItemIdentity(Any item)
    {
        try
        {
            static string Id(KIID? id) => id?.Value ?? string.Empty;
            if (item.Is(Track.Descriptor)) return Id(item.Unpack<Track>().Id);
            if (item.Is(Arc.Descriptor)) return Id(item.Unpack<Arc>().Id);
            if (item.Is(Via.Descriptor)) return Id(item.Unpack<Via>().Id);
            if (item.Is(BoardGraphicShape.Descriptor)) return Id(item.Unpack<BoardGraphicShape>().Id);
            if (item.Is(Barcode.Descriptor)) return Id(item.Unpack<Barcode>().Id);
            if (item.Is(BoardText.Descriptor)) return Id(item.Unpack<BoardText>().Id);
            if (item.Is(BoardTextBox.Descriptor)) return Id(item.Unpack<BoardTextBox>().Id);
            if (item.Is(Pad.Descriptor)) return Id(item.Unpack<Pad>().Id);
            if (item.Is(Zone.Descriptor)) return Id(item.Unpack<Zone>().Id);
            if (item.Is(Dimension.Descriptor)) return Id(item.Unpack<Dimension>().Id);
            if (item.Is(ReferenceImage.Descriptor)) return Id(item.Unpack<ReferenceImage>().Id);
            if (item.Is(GridItem.Descriptor)) return Id(item.Unpack<GridItem>().Id);
            if (item.Is(Group.Descriptor)) return Id(item.Unpack<Group>().Id);
            if (item.Is(FootprintInstance.Descriptor)) return Id(item.Unpack<FootprintInstance>().Id);
            if (item.Is(Table.Descriptor)) return Id(item.Unpack<Table>().Id);
        }
        catch (InvalidProtocolBufferException)
        {
            throw Invalid("A checked PCB item identity payload could not be decoded.");
        }
        return null;
    }

    private static bool Kiid(KIID? id) => id is not null && Uuid(id.Value);

    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && id.ToString("D") == value;

    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static AutomationException Invalid(string message) => new("invalid_checked_pcb_item", message);
}
