using System.Collections.Immutable;
using M = KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

/// <summary>Level drafts, removals and level rebases (contract rbg-v2 sections 4.6-4.8) on the wire.</summary>
public static partial class RecursiveBlockCodec
{
    public static M.RecursiveLevelDraft Decode(P.LevelDraftData data, Guid documentId)
    {
        Known(data, P.LevelDraftData.Parser);
        return new(Decode(Need(data.Scope), documentId), [.. data.ChildDrafts.Select(d => Decode(d, documentId))],
            [.. data.ConnectionDrafts.Select(d => Decode(d, documentId))],
            [.. data.NewChildren.Select(c => new M.NewBlockOccurrence(Selection(Need(c.Selection)), GuidValue(c.RequirementRevisionId),
                c.ImplementationName, c.Name, Fields(Need(c.Fields)), [.. c.Interfaces.Select(Interface)],
                c.Definition is { } definition ? Decode(definition) : null))],
            [.. data.NewConnections.Select(c => new M.NewConnectionOccurrence(Selection(Need(c.Selection)), GuidValue(c.RequirementRevisionId),
                c.ImplementationName, c.Name, Defined((M.DiagramConnectionKind)((int)c.Kind - 1)), Domain(c.Domain), Direction(c.Direction),
                [.. c.Endpoints.Select(Decode)], Fields(Need(c.Fields)), c.Realization is { } realization ? Realization(realization) : null,
                c.HasMemberOf ? GuidValue(c.MemberOf) : null))]);
    }

    public static P.LevelDraftData Encode(M.RecursiveLevelDraft draft)
    {
        var data = new P.LevelDraftData { Scope = Encode(draft.Scope) };
        data.ChildDrafts.Add(draft.ChildDrafts.Select(Encode));
        data.ConnectionDrafts.Add(draft.ConnectionDrafts.Select(Encode));
        data.NewChildren.Add(draft.NewChildren.Select(c =>
        {
            var row = new P.NewBlockOccurrenceData { Selection = Selection(c.Selection), RequirementRevisionId = Id(c.RequirementRevisionId),
                ImplementationName = c.ImplementationName, Name = c.Name, Fields = Fields(c.Requirements) };
            row.Interfaces.Add(c.Interfaces.Select(Interface));
            if (c.Definition is { } definition) row.Definition = Encode(definition);
            return row;
        }));
        data.NewConnections.Add(draft.NewConnections.Select(c =>
        {
            var row = new P.NewConnectionData { Selection = Selection(c.Selection), RequirementRevisionId = Id(c.RequirementRevisionId),
                ImplementationName = c.ImplementationName, Name = c.Name, Kind = (P.DiagramConnectionKind)((int)c.Kind + 1),
                Domain = (P.DiagramDomain)c.Domain, Direction = (P.DiagramConnectionDirection)c.Direction, Fields = Fields(c.Requirements) };
            row.Endpoints.Add(c.Endpoints.Select(Encode));
            if (c.Realization is { } realization) row.Realization = Realization(realization);
            if (c.MemberOf is { } parent) row.MemberOf = Id(parent);
            return row;
        }));
        return data;
    }

    /// <summary>A level save request: the draft, the exact root and level path, and the identities of
    /// the revisions it may create (one pair per child and connection draft, none for unchanged drafts).</summary>
    public static (M.BlockSelection ExpectedRoot, ImmutableArray<M.BlockSelection> Path, M.RecursiveLevelDraft Draft, M.LevelRevisionIds Ids,
        M.RequirementRevisionOrigin Origin, ImmutableArray<M.DiagramRequirementResolution> Resolutions, bool SelectImplementation)
        Decode(P.SaveLevelDraftData data, Guid documentId)
    {
        Known(data, P.SaveLevelDraftData.Parser);
        ImmutableDictionary<Guid, M.LevelRevisionId> Pairs(IEnumerable<P.RevisionIdAssignmentData> rows)
        {
            var pairs = ImmutableDictionary.CreateBuilder<Guid, M.LevelRevisionId>();
            foreach (var row in rows)
                if (!pairs.TryAdd(GuidValue(row.ObjectId), new(GuidValue(row.NewRevisionId), GuidValue(row.NewRequirementRevisionId))))
                    throw Invalid("Assign one revision identity pair per edited child or connection.");
            return pairs.ToImmutable();
        }
        var ids = new M.LevelRevisionIds(GuidValue(data.NewRevisionId), GuidValue(data.NewRequirementRevisionId),
            [.. data.AncestorRevisionIds.Select(GuidValue)], Pairs(data.ChildRevisions), Pairs(data.ConnectionRevisions));
        return (Selection(Need(data.ExpectedRoot)), [.. data.BlockPath.Select(Selection)], Decode(Need(data.Draft), documentId), ids,
            Origin(Need(data.Origin)), [.. data.Resolutions.Select(Decode)], data.SelectImplementation);
    }

    public static (M.BlockSelection ExpectedRoot, ImmutableArray<M.BlockSelection> Path, M.RecursiveLevelDraft Draft, M.LevelEditCommand Command)
        Decode(P.LevelEditCommandData data, Guid documentId)
    {
        Known(data, P.LevelEditCommandData.Parser);
        var kind = data.Kind switch
        {
            P.LevelEditCommandKind.LeckRemoveChild => M.LevelEditCommandKind.RemoveChild,
            P.LevelEditCommandKind.LeckRemoveConnection => M.LevelEditCommandKind.RemoveConnection,
            P.LevelEditCommandKind.LeckRemoveInterface => M.LevelEditCommandKind.RemoveInterface,
            // Lane 2B band (Round A3): signals of one connection.
            P.LevelEditCommandKind.LeckRemoveConnectionMembers => M.LevelEditCommandKind.RemoveConnectionMembers,
            _ => throw Invalid("Choose a supported removal: a child block, a connection, an interface or signals of a connection.")
        };
        var command = new M.LevelEditCommand(kind, data.HasBlockId ? GuidValue(data.BlockId) : null,
            data.HasConnectionId ? GuidValue(data.ConnectionId) : null, data.HasInterfaceId ? GuidValue(data.InterfaceId) : null,
            data.DetachConnections, Origin(Need(data.Origin)), [.. data.MemberIds.Select(GuidValue)]);
        return (Selection(Need(data.ExpectedRoot)), [.. data.BlockPath.Select(Selection)], Decode(Need(data.Draft), documentId), command);
    }

    public static P.LevelEditResultData Encode(M.RecursiveLevelDraft draft, ImmutableArray<M.LevelEditEffect> effects)
    {
        var data = new P.LevelEditResultData { Draft = Encode(draft) };
        data.Effects.Add(effects.Select(Encode));
        return data;
    }

    public static (M.RecursiveLevelDraft Draft, ImmutableArray<M.DiagramRequirementResolution> Resolutions) Decode(P.RebaseLevelData data, Guid documentId)
    {
        Known(data, P.RebaseLevelData.Parser);
        return (Decode(Need(data.Draft), documentId), [.. data.Resolutions.Select(Decode)]);
    }

    public static P.LevelMergeData Encode(M.RecursiveLevelMerge merge, M.RecursiveLevelMergeResult result)
    {
        var data = new P.LevelMergeData { ExpectedRoot = Selection(result.ExpectedRoot), OriginalDraft = Encode(merge.OriginalDraft),
            BaseContextVersion = checked((uint)merge.BaseContextVersion), SavedContextVersion = checked((uint)merge.SavedContextVersion) };
        if (merge.SavedOrigin is { } origin) data.SavedOrigin = Origin(origin);
        data.BlockPath.Add(result.Path.Select(Selection));
        if (result.Candidate is { } candidate) data.Candidate = Encode(candidate);
        data.Conflicts.Add(result.Conflicts.Select(c =>
        {
            var row = new P.LevelConflictData { Kind = (P.LevelConflictKind)((int)c.Kind + 1), OwnerId = Id(c.OwnerId),
                Baseline = c.Baseline, Draft = c.Draft, Saved = c.Saved, Message = c.Message,
                Field = c.Field is { } field ? (P.RequirementFieldKind)((int)field + 1) : P.RequirementFieldKind.RfkUnknown };
            if (c.OwnerStateId is { } state) row.OwnerStateId = Id(state);
            if (c.ObjectId is { } target) row.ObjectId = Id(target);
            return row;
        }));
        data.PresentationOverrides.Add(result.Overrides.Select(o => new P.PresentationOverrideData { ElementKey = o.ElementKey, SavedValueJson = o.SavedValueJson }));
        return data;
    }

    /// <summary>What a level save created, for the result's save summary.</summary>
    public static P.SaveSummaryData Summary(M.RecursiveLevelSaveResult result)
    {
        var summary = new P.SaveSummaryData { Changed = result.Changed, PrunedPresentationEntries = checked((uint)result.PrunedPresentationEntries),
            SelectedRoot = Selection(result.Graph.SelectedRoot) };
        summary.CreatedBlockRevisions.Add(result.CreatedBlockRevisions.Select(Selection));
        summary.CreatedConnectionRevisions.Add(result.CreatedConnectionRevisions.Select(Selection));
        summary.CreatedAncestors.Add(result.CreatedAncestors.Select(Selection));
        return summary;
    }

    private static P.DiagramBoundaryInterfaceData Interface(M.DiagramBoundaryInterface i) => new() { Id = Id(i.Id), Name = i.Name, Intent = i.Intent,
        Domain = (P.DiagramDomain)i.Domain, Direction = (P.DiagramInterfaceDirection)i.Direction };
    private static M.DiagramBoundaryInterface Interface(P.DiagramBoundaryInterfaceData i) => new(GuidValue(i.Id), i.Name, i.Intent,
        Defined((M.DiagramDomain)(int)i.Domain), Defined((M.DiagramInterfaceDirection)(int)i.Direction));
}
