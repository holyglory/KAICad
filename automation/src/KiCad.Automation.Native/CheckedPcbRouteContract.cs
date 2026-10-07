using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;

namespace KiCad.Automation.Native;

/// <summary>
/// Pure validation for the checked PCB route request/receipt boundary.
///
/// This class deliberately does not contact KiCad.  It makes the native
/// receipt useful to a caller that has lost a reply, and prevents a forged or
/// stale protobuf from being presented as a committed route.
/// </summary>
public static class CheckedPcbRouteContract
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumResultBytes = 4 * 1024 * 1024;
    private const int MinimumResultBytes = 64 * 1024;

    public static bool HasPreviewDelta(PcbRoutePreviewState preview) => preview.RouteItems.Count != 0
        || (preview.ReplacementUpdates?.Items.Count ?? 0) != 0 || preview.RemovedItemIds.Count != 0
        || preview.GroupChanges.Count != 0;

    public static void ValidatePreview(PcbRoutePreviewState preview, bool inspectStateOnly = false)
    {
        if (preview.NativeCommit || !preview.PreviewOnly)
            throw Invalid("A routing preview must remain detached and cannot report a native commit.");
        bool hasDelta = HasPreviewDelta(preview);
        if (inspectStateOnly && !hasDelta) return;
        if (preview.CompleteDeltaVersion is not (1 or 2))
            throw new AutomationException("route_preview_contract_unsupported",
                "This native preview does not provide a supported complete route-delta contract; update the native editor before using its routing candidates.");
        if (preview.GroupChanges.Count != 0 && preview.CompleteDeltaVersion != 2)
            throw new AutomationException("route_preview_contract_unsupported",
                "Grouped route candidates require complete route-delta version 2.");
        ValidateGroupChanges(preview.GroupChanges, preview.RouteItems, preview.ReplacementUpdates, preview.RemovedItemIds);
        if (!preview.Completed || preview.ErrorCode.Length != 0 || preview.Cancelled || preview.Reconciled || !hasDelta)
            throw Invalid("Only a successful, current complete native route delta can expose actionable candidates.");
    }

    /// <summary>Validate a checked route request against the current native process epoch.</summary>
    /// <param name="inspect">When true, allow a read-only receipt inspection before the
    /// board has complete file-baseline coverage. Mutation admission requires the
    /// full serialized state plus unchanged file baselines; it does not rely on
    /// the partial numeric PCB cache cursor.</param>
    public static void ValidateRequest(CheckedPcbRouteCommit request, string processEpoch, bool inspect = false)
    {
        if (request is null)
            throw Invalid("A checked PCB route request is required.");

        var candidate = request.Candidate;
        var expected = request.ExpectedState;
        var document = candidate?.Header?.Document;
        if (candidate is null || expected is null || candidate.Header is null || document is null)
            throw Invalid("A checked PCB route requires a candidate and complete expected board state.");

        if (!Uuid(processEpoch) || !Uuid(request.OperationId) || !Uuid(request.DrcJobId)
            || !Uuid(expected.ProcessEpoch) || expected.ProcessEpoch != processEpoch)
            throw Invalid("The checked route must identify one canonical operation, DRC job and native process epoch.");

        if (document.Type != DocumentType.DoctypePcb || string.IsNullOrWhiteSpace(document.BoardFilename)
            || document.Project is null || string.IsNullOrWhiteSpace(document.Project.Name)
            || !Path.IsPathFullyQualified(document.Project.Path))
            throw Invalid("The checked route must target one explicit PCB project and board file.");

        if (candidate.Container is not null || candidate.Header.Container is not null
            || candidate.Header.FieldMask is not null
            || candidate.Items.Any(item => !RouteItem(item))
            || (candidate.Items.Count == 0 && (request.ReplacementUpdates?.Items.Count ?? 0) == 0
                && (request.Removals?.ItemIds.Count ?? 0) == 0))
            throw Invalid("The checked route candidate must contain only board-level Track, Arc or Via items.");

        var candidateIds = candidate.Items.Select(RouteItemId).ToHashSet(StringComparer.Ordinal);
        if (candidateIds.Count != candidate.Items.Count)
            throw Invalid("New route items require distinct UUIDs.");
        var updateIds = new HashSet<string>(StringComparer.Ordinal);
        if (request.ReplacementUpdates is not null)
        {
            var updates = request.ReplacementUpdates;
            if (updates.Header?.Document is null || !updates.Header.Document.Equals(document)
                || updates.Header.Container is not null || updates.Header.FieldMask is not null
                || updates.Items.Count == 0 || updates.Items.Any(item => !RouteItem(item)))
                throw Invalid("Identity-preserving route replacements must target the same board and contain only Track, Arc or Via items.");
            foreach (string id in updates.Items.Select(RouteItemId))
                if (!updateIds.Add(id) || candidateIds.Contains(id))
                    throw Invalid("Identity-preserving route replacements require unique UUIDs distinct from newly-created route items.");
        }
        if (request.Removals is not null)
        {
            var removals = request.Removals;
            if (removals.Header?.Document is null || !removals.Header.Document.Equals(document)
                || removals.Header.Container is not null || removals.Header.FieldMask is not null
                || removals.ItemIds.Count == 0)
                throw Invalid("Route removals must identify existing copper on the same board.");
            var removedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in removals.ItemIds)
                if (!Kiid(id) || !removedIds.Add(id.Value) || candidateIds.Contains(id.Value) || updateIds.Contains(id.Value))
                    throw Invalid("Route creates, updates and removals require distinct UUIDs.");
        }

        ValidateGroupChanges(request.GroupChanges, candidate.Items,
            request.ReplacementUpdates, request.Removals?.ItemIds);

        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.ProjectSettingsIncluded
            || expected.Document is null || !expected.Document.Equals(document)
            || expected.Revision is null || !Uuid(expected.NativeIdentity)
            || expected.Revision.Epoch != expected.NativeIdentity || !Digest(expected.StateSha256))
            throw Invalid("The checked route must match an exact PCB lifecycle observation.");

        if (!inspect && (!expected.DiskBaselineChecked || !CheckedSchematicContract.FileCoverage(expected)))
            throw Invalid("The checked PCB state lacks file-baseline coverage.");

        if (request.MaximumResultBytes != 0
            && (request.MaximumResultBytes < MinimumResultBytes || request.MaximumResultBytes > MaximumResultBytes))
            throw Invalid("The checked route receipt allowance must be zero or between 64 KiB and 4 MiB.");
        if (request.CalculateSize() > MaximumRequestBytes)
            throw Invalid("The checked route request exceeds its bounded 4 MiB size allowance.");
    }

    /// <summary>
    /// Validate a native route receipt.  A NOT_FOUND receipt is accepted only
    /// for an explicit read/inspection operation and never supplies mutation
    /// evidence.  INDETERMINATE remains intentionally non-committal: callers
    /// must inspect native state rather than treating it as a rejection.
    /// </summary>
    public static void ValidateResult(CheckedPcbRouteCommit request, CheckedPcbRouteReceipt result, bool inspect)
    {
        if (request is null || result is null)
            throw Invalid("A checked route request and receipt are both required.");

        // Identity is checked before interpreting status.  A receipt from a
        // different board, process or operation can never explain this call.
        ValidateRequest(request, request.ExpectedState?.ProcessEpoch ?? string.Empty, inspect: true);
        if (result.Document is null || !result.Document.Equals(request.Candidate!.Header!.Document)
            || result.ProcessEpoch != request.ExpectedState!.ProcessEpoch
            || result.OperationId != request.OperationId
            || !System.Enum.IsDefined(result.State) || result.State == CheckedPcbRouteReceipt.Types.State.Unknown)
            throw Invalid("The native checked route receipt does not identify the exact request.");

        if (result.Removals is not null && (request.Removals is null || !result.Removals.Equals(request.Removals)))
            throw Invalid("The native receipt contains removals from another request.");
        if (result.RemovedResult is not null && result.Removals is null)
            throw Invalid("A native deletion result must identify its exact removal request.");
        if (!result.GroupChanges.Equals(request.GroupChanges))
            throw Invalid("The native receipt contains grouped-route changes from another request.");

        // Inspection may read a NOT_FOUND receipt for a partially observed
        // board.  Any receipt that claims the native operation was admitted
        // must satisfy the full mutation admission contract.
        if (result.State != CheckedPcbRouteReceipt.Types.State.NotFound)
            ValidateRequest(request, request.ExpectedState.ProcessEpoch, inspect: false);

        switch (result.State)
        {
            case CheckedPcbRouteReceipt.Types.State.NotFound:
                if (!inspect || result.RequestVerified || result.Created is not null
                    || result.Before is not null || result.After is not null
                    || result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0 || result.Replayed
                    || result.Updated is not null || result.UpdatedResult is not null
                    || result.Removals is not null || result.RemovedResult is not null)
                    throw Invalid("A missing route receipt is read-only and cannot claim a verified result.");
                return;

            case CheckedPcbRouteReceipt.Types.State.Completed:
                ValidateCompleted(request, result);
                return;

            case CheckedPcbRouteReceipt.Types.State.Rejected:
                ValidateRejected(request, result);
                return;

            case CheckedPcbRouteReceipt.Types.State.Indeterminate:
                ValidateIndeterminate(request, result);
                return;

            default:
                throw Invalid("The native checked route receipt has an unknown state.");
        }
    }

    private static void ValidateCompleted(CheckedPcbRouteCommit request, CheckedPcbRouteReceipt result)
    {
        var expected = request.ExpectedState;
        var before = result.Before;
        var after = result.After;
        var created = result.Created;
        if (!result.RequestVerified || before is null || after is null || created is null
            || result.ErrorCode.Length != 0 || result.ErrorMessage.Length != 0)
            throw Invalid("A completed checked route must carry a verified, error-free native receipt.");

        if (!before.Equals(expected) || !StateIdentity(after, request.Candidate.Header.Document, expected)
            || after.Revision is null || expected.Revision is null
            || after.Revision.Epoch != expected.Revision.Epoch
            || after.Revision.Sequence <= expected.Revision.Sequence
            || !after.DiskBaselineChecked
            || !CheckedSchematicContract.FileCoverage(after) || !Digest(after.StateSha256))
            throw Invalid("A completed checked route lacks a consistent post-commit lifecycle state.");

        if (created.CreatedItems.Count != request.Candidate.Items.Count
            || created.Status != ItemRequestStatus.IrsOk)
            throw Invalid("A completed checked route must report every requested item.");

        if (request.ReplacementUpdates is not null)
        {
            if (result.Updated is null || !result.Updated.Equals(request.ReplacementUpdates)
                || result.UpdatedResult is null
                || result.UpdatedResult.Status != ItemRequestStatus.IrsOk
                || result.UpdatedResult.UpdatedItems.Count != request.ReplacementUpdates.Items.Count)
                throw Invalid("A completed checked route must report the exact identity-preserving update request and native response.");

            for (int index = 0; index < request.ReplacementUpdates.Items.Count; index++)
            {
                var actual = result.UpdatedResult.UpdatedItems[index];
                if (actual.Status is null || actual.Status.Code != ItemStatusCode.IscOk || actual.Item is null
                    || !RouteItemEqual(request.ReplacementUpdates.Items[index], actual.Item))
                    throw Invalid("A completed checked route returned unexpected native geometry for an identity-preserving update.");
            }
        }

        if (request.Removals is not null)
        {
            if (result.Removals is null || !result.Removals.Equals(request.Removals)
                || result.RemovedResult is null || result.RemovedResult.Status != ItemRequestStatus.IrsOk
                || result.RemovedResult.DeletedItems.Count != request.Removals.ItemIds.Count)
                throw Invalid("A completed checked route must report the exact removal request and every native deletion result.");
            var expectedRemoved = request.Removals.ItemIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var item in result.RemovedResult.DeletedItems)
                if (item.Id is null || item.Status != ItemDeletionStatus.IdsOk || !expectedRemoved.Remove(item.Id.Value))
                    throw Invalid("A completed checked route contains an unexpected or unsuccessful removal.");
            if (expectedRemoved.Count != 0)
                throw Invalid("A completed checked route did not remove every requested item.");
        }
        else if (result.Removals is not null || result.RemovedResult is not null)
            throw Invalid("A completed additive route must not claim unrequested removals.");

        if (request.GroupChanges.Count != 0)
        {
            if (result.GroupChanges.Count != request.GroupChanges.Count
                || result.AfterGroupStates.Count != request.GroupChanges.Count)
                throw Invalid("A completed grouped route must report its exact group request and native after-state.");
            for (int index = 0; index < request.GroupChanges.Count; index++)
            {
                var change = request.GroupChanges[index];
                if (!result.GroupChanges[index].Equals(change)
                    || !result.AfterGroupStates[index].Equals(change.ExpectedAfter))
                    throw Invalid("A completed grouped route returned an unexpected native group membership state.");
            }
        }
        else if (result.GroupChanges.Count != 0 || result.AfterGroupStates.Count != 0)
            throw Invalid("An ungrouped route must not claim grouped-route state.");

        for (int index = 0; index < request.Candidate.Items.Count; index++)
        {
            var item = created.CreatedItems[index];
            if (item.Status is null || item.Status.Code != ItemStatusCode.IscOk || item.Item is null
                || !RouteItemEqual(request.Candidate.Items[index], item.Item))
                throw Invalid("A completed checked route returned an unexpected Track, Arc or Via item.");
        }
    }

    private static void ValidateRejected(CheckedPcbRouteCommit request, CheckedPcbRouteReceipt result)
    {
        if (!result.RequestVerified || result.Created is not null || string.IsNullOrWhiteSpace(result.ErrorCode))
            throw Invalid("A rejected checked route must be verified, explain its refusal and contain no created items.");

        // A rejection may preserve the before checkpoint for diagnosis.  It
        // cannot claim a changed after-state; that is indeterminate instead.
        if (result.Before is not null && !result.Before.Equals(request.ExpectedState))
            throw Invalid("A rejected checked route contains a before-state for another revision.");
        if (result.After is not null && !result.After.Equals(request.ExpectedState))
            throw Invalid("A rejected checked route cannot claim a changed native after-state.");
    }

    private static void ValidateIndeterminate(CheckedPcbRouteCommit request, CheckedPcbRouteReceipt result)
    {
        if (!result.RequestVerified || string.IsNullOrWhiteSpace(result.ErrorCode))
            throw Invalid("An indeterminate checked route must remain verified as uncertain and explain why.");

        // Do not require Created/Before/After to be absent: the native owner
        // deliberately preserves partial evidence so a caller can reconcile a
        // lost reply.  In particular, this state never means "no mutation".
        if (result.Before is not null && !result.Before.Equals(request.ExpectedState))
            throw Invalid("An indeterminate checked route contains a before-state for another revision.");
        if (result.After is not null && !StateIdentity(result.After, request.Candidate.Header.Document,
                                                        request.ExpectedState))
            throw Invalid("An indeterminate checked route contains an after-state for another document or process.");
    }

    private static bool StateIdentity(DocumentLifecycleState state, DocumentSpecifier document,
                                      DocumentLifecycleState expected)
    {
        return state.Document is not null && state.Document.Equals(document)
            && state.ProcessEpoch == expected.ProcessEpoch && state.Scope == DocumentLifecycleScope.DlsPcb
            && state.NativeIdentity == expected.NativeIdentity && state.ProjectSettingsIncluded
            && state.Revision is not null && Uuid(state.NativeIdentity);
    }

    private static bool RouteItem(Any item)
    {
        try
        {
            if (item is null) return false;
            if (item.Is(Track.Descriptor))
            {
                var track = item.Unpack<Track>();
                return UniqueProperties(track.CustomProperties) && Kiid(track.Id) && track.Start is not null && track.End is not null
                    && track.Width is not null && track.Width.ValueNm > 0
                    && track.Layer != BoardLayer.BlUnknown && track.Layer != BoardLayer.BlUndefined
                    && track.Layer != BoardLayer.BlUnselected;
            }
            if (item.Is(Arc.Descriptor))
            {
                var arc = item.Unpack<Arc>();
                return UniqueProperties(arc.CustomProperties) && Kiid(arc.Id) && arc.Start is not null && arc.Mid is not null && arc.End is not null
                    && arc.Width is not null && arc.Width.ValueNm > 0
                    && arc.Layer != BoardLayer.BlUnknown && arc.Layer != BoardLayer.BlUndefined
                    && arc.Layer != BoardLayer.BlUnselected;
            }
            if (item.Is(Via.Descriptor))
            {
                var via = item.Unpack<Via>();
                return UniqueProperties(via.CustomProperties) && Kiid(via.Id) && via.Position is not null && via.PadStack is not null;
            }
            return false;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static void ValidateGroupChanges(
        IEnumerable<NativeRouteGroupChange> changes,
        IEnumerable<Any> candidates,
        UpdateItems? updates,
        IEnumerable<KIID>? removals)
    {
        var candidateIds = candidates.Select(RouteItemId).ToHashSet(StringComparer.Ordinal);
        var updateIds = updates?.Items.Select(RouteItemId).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var removedIds = removals?.Select(id => id.Value).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.ExpectedBefore is null || change.ExpectedAfter is null
                || change.ExpectedBefore.Group is null || change.ExpectedAfter.Group is null
                || change.ExpectedBefore.Kind != NativeRouteGroupKind.NrgkOrdinary
                || change.ExpectedAfter.Kind != NativeRouteGroupKind.NrgkOrdinary
                || change.ExpectedBefore.ParentGroupId is not null || change.ExpectedAfter.ParentGroupId is not null)
                throw Invalid("Grouped route changes support only direct ordinary PCB groups.");
            var before = change.ExpectedBefore.Group;
            var after = change.ExpectedAfter.Group;
            if (!Kiid(before.Id) || !Kiid(after.Id) || before.Id.Value != after.Id.Value
                || !groupIds.Add(before.Id.Value) || before.LibId is not null || after.LibId is not null)
                throw Invalid("Grouped route changes require distinct ordinary groups with stable identities.");
            if (!GroupMetadataEqual(before, after))
                throw Invalid("Grouped route changes may change direct members only; group metadata must remain exact.");
            var beforeMembers = before.Items.Select(RouteId).ToArray();
            var afterMembers = after.Items.Select(RouteId).ToArray();
            if (beforeMembers.Any(id => !Uuid(id)) || afterMembers.Any(id => !Uuid(id))
                || beforeMembers.Distinct(StringComparer.Ordinal).Count() != beforeMembers.Length
                || afterMembers.Distinct(StringComparer.Ordinal).Count() != afterMembers.Length)
                throw Invalid("Grouped route membership must contain distinct canonical item identities.");
            foreach (var source in change.CreatedSources)
            {
                if (!Kiid(source.CreatedItemId) || !Kiid(source.SourceItemId) || !Kiid(source.SourceGroupId)
                    || source.SourceGroupId.Value != before.Id.Value
                    || !createdIds.Add(source.CreatedItemId.Value)
                    || !candidateIds.Contains(source.CreatedItemId.Value)
                    || !afterMembers.Contains(source.CreatedItemId.Value, StringComparer.Ordinal)
                    || (!removedIds.Contains(source.SourceItemId.Value) && !updateIds.Contains(source.SourceItemId.Value)))
                    throw Invalid("Every grouped route child must identify one requested source and ordinary source group.");
            }
        }
    }

    private static bool GroupMetadataEqual(Group left, Group right)
    {
        left = left.Clone(); right = right.Clone();
        left.Items.Clear(); right.Items.Clear();
        return left.Equals(right);
    }

    private static string RouteId(KIID id) => id?.Value ?? string.Empty;

    private static bool RouteItemEqual(Any expected, Any actual)
    {
        try
        {
            if (expected.Is(Track.Descriptor) && actual.Is(Track.Descriptor))
            {
                var left = expected.Unpack<Track>(); var right = actual.Unpack<Track>();
                if (!UniqueProperties(left.CustomProperties) || !UniqueProperties(right.CustomProperties)) return false;
                Normalize(left); Normalize(right);
                return left.Equals(right);
            }
            if (expected.Is(Arc.Descriptor) && actual.Is(Arc.Descriptor))
            {
                var left = expected.Unpack<Arc>(); var right = actual.Unpack<Arc>();
                if (!UniqueProperties(left.CustomProperties) || !UniqueProperties(right.CustomProperties)) return false;
                Normalize(left); Normalize(right);
                return left.Equals(right);
            }
            if (expected.Is(Via.Descriptor) && actual.Is(Via.Descriptor))
            {
                var left = expected.Unpack<Via>(); var right = actual.Unpack<Via>();
                if (!UniqueProperties(left.CustomProperties) || !UniqueProperties(right.CustomProperties)) return false;
                Normalize(left); Normalize(right);
                return left.Equals(right);
            }
            return false;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static bool UniqueProperties(IEnumerable<CustomProperty> properties)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return properties.All(property => keys.Add(property.Key));
    }

    private static void NormalizeProperties(Google.Protobuf.Collections.RepeatedField<CustomProperty> properties)
    {
        var ordered = properties.OrderBy(property => property.Key, StringComparer.Ordinal).ToArray();
        properties.Clear(); properties.Add(ordered);
    }

    // Normalize only values whose exact native serialization is known. Full
    // typed equality still compares zero coordinates, false flags and empty
    // repeated properties. Read-only parent metadata is not writable state.
    private static void Normalize(Track item)
    {
        NormalizeProperties(item.CustomProperties);
        item.Parent = null;
        if (item.Locked == LockedState.LsUnknown) item.Locked = LockedState.LsUnlocked;
        item.Net ??= new Net();
        if (item.Net.Name.Length != 0) item.Net.Code = null;
    }

    private static void Normalize(Arc item)
    {
        NormalizeProperties(item.CustomProperties);
        item.Parent = null;
        if (item.Locked == LockedState.LsUnknown) item.Locked = LockedState.LsUnlocked;
        item.Net ??= new Net();
        if (item.Net.Name.Length != 0) item.Net.Code = null;
    }

    private static void Normalize(Via item)
    {
        NormalizeProperties(item.CustomProperties);
        item.Parent = null;
        if (item.Locked == LockedState.LsUnknown) item.Locked = LockedState.LsUnlocked;
        item.Net ??= new Net();
        if (item.Net.Name.Length != 0) item.Net.Code = null;
        item.Teardrop ??= new PadTeardropSettings
        {
            Mode = PadTeardropMode.PtmDisabled, AllowMultipleTrackSegments = true, PreferZoneConnection = true,
            MaxLength = new() { ValueNm = 1_000_000 }, MaxWidth = new() { ValueNm = 2_000_000 },
            BestLengthRatio = 0.5, BestWidthRatio = 1.0, MaxTrackWidthRatio = 0.9
        };
        if (item.Teardrop.Mode == PadTeardropMode.PtmUnknown) item.Teardrop.Mode = PadTeardropMode.PtmDisabled;
        item.Teardrop.MaxLength ??= new Distance();
        item.Teardrop.MaxWidth ??= new Distance();
        if (item.PadStack is not { } stack) return;
        if (stack.Type == PadStackType.PstUnknown) stack.Type = PadStackType.PstNormal;
        if (stack.UnconnectedLayerRemoval == UnconnectedLayerRemoval.UlrUnknown)
            stack.UnconnectedLayerRemoval = UnconnectedLayerRemoval.UlrKeep;
        stack.Angle ??= new Angle();
        if (stack.Drill is { Shape: DrillShape.DsUnknown }) stack.Drill.Shape = DrillShape.DsUndefined;
        foreach (var layer in stack.CopperLayers)
        {
            layer.Offset ??= new Vector2();
            if (layer.Shape == PadStackShape.PssUnknown) layer.Shape = PadStackShape.PssCircle;
            if (layer.CustomAnchorShape == PadStackShape.PssUnknown) layer.CustomAnchorShape = PadStackShape.PssCircle;
        }
        if (stack.ZoneSettings is null)
        {
            var shape = stack.CopperLayers.FirstOrDefault()?.Shape ?? PadStackShape.PssCircle;
            stack.ZoneSettings = new ZoneConnectionSettings
            {
                ZoneConnection = ZoneConnectionStyle.ZcsInherited,
                ThermalSpokes = new() { Angle = new() { ValueDegrees = shape is PadStackShape.PssOval
                    or PadStackShape.PssRectangle or PadStackShape.PssRoundrect or PadStackShape.PssChamferedrect ? 90 : 45 } }
            };
        }
        else
        {
            if (stack.ZoneSettings.ZoneConnection == ZoneConnectionStyle.ZcsUnknown)
                stack.ZoneSettings.ZoneConnection = ZoneConnectionStyle.ZcsInherited;
            stack.ZoneSettings.ThermalSpokes ??= new() { Angle = new() { ValueDegrees = 45 } };
            stack.ZoneSettings.ThermalSpokes.Angle ??= new Angle();
        }
    }

    private static string RouteItemId(Any item)
    {
        try
        {
            if (item.Is(Track.Descriptor)) return item.Unpack<Track>().Id?.Value ?? string.Empty;
            if (item.Is(Arc.Descriptor)) return item.Unpack<Arc>().Id?.Value ?? string.Empty;
            if (item.Is(Via.Descriptor)) return item.Unpack<Via>().Id?.Value ?? string.Empty;
        }
        catch (InvalidProtocolBufferException)
        {
            // The normal RouteItem validator reports the detailed malformed
            // payload error; this helper only extracts the identity set.
        }

        return string.Empty;
    }

    private static bool Kiid(KIID? id) => id is not null && Uuid(id.Value);

    private static bool Uuid(string? value) => Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && id.ToString("D") == value;

    private static bool Digest(string? value) => value?.Length == 64
        && value.All(character => (character >= '0' && character <= '9')
                                  || (character >= 'a' && character <= 'f'));

    private static AutomationException Invalid(string message) => new("invalid_checked_pcb_route", message);
}
