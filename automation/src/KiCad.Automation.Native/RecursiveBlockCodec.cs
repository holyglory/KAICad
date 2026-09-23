using System.Collections.Immutable;
using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using M = KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;
using S = KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Native;

/// <summary>Lossless shared C++/.NET messages. Unsupported fields and precision are
/// rejected, not simplified into a success. No I/O or agent execution occurs here.</summary>
public static class RecursiveBlockCodec
{
    public static P.RequirementMergeData Encode(M.RecursiveRequirementMerge merge,
        IEnumerable<M.DiagramRequirementResolution>? choices = null)
    {
        var resolved = merge.Inspect(choices);
        var result = new P.RequirementMergeData { ExpectedRoot = Selection(resolved.ExpectedRoot),
            OriginalDraft = Encode(merge.OriginalDraft), SavedDraft = Encode(merge.SavedDraft),
            BaseContextVersion = checked((uint)merge.BaseContextVersion), SavedContextVersion = checked((uint)merge.SavedContextVersion), SavedOrigin = Origin(merge.SavedOrigin) };
        result.BlockPath.Add(resolved.BlockPath.Select(Selection));
        if (resolved.Candidate is { } candidate) result.Candidate = Encode(candidate);
        result.Conflicts.Add(resolved.Conflicts.Select(c => new P.RequirementConflictData
            { Field = (P.RequirementFieldKind)((int)c.Field + 1), Baseline = c.Base, Draft = c.Draft, Saved = c.Saved }));
        return result;
    }

    public static M.DiagramRequirementResolution Decode(P.RequirementResolutionData resolution)
    {
        Known(resolution, P.RequirementResolutionData.Parser);
        return new(new(GuidValue(resolution.DocumentId), GuidValue(resolution.OwnerId), GuidValue(resolution.StateId)),
            GuidValue(resolution.BaselineRevisionId), GuidValue(resolution.SavedRevisionId), Fields(Need(resolution.Baseline)),
            Fields(Need(resolution.Draft)), Fields(Need(resolution.Saved)), (M.DiagramRequirementField)((int)resolution.Field - 1), resolution.Text);
    }

    public static P.BlockDefinitionData Encode(M.BlockDefinition definition)
    {
        definition.Validate();
        var result = new P.BlockDefinitionData
        {
            Purpose = EncodeChoice(definition.Purpose), Type = EncodeChoice(definition.Type),
            Manufacturer = EncodeChoice(definition.Manufacturer), Family = EncodeChoice(definition.Family),
            Model = EncodeChoice(definition.Model), OrderablePart = EncodeChoice(definition.OrderablePart), Package = EncodeChoice(definition.Package)
        };
        if (definition.KnowledgeClass is { } choice)
        {
            result.KnowledgeClass = new() { State = (P.DefinitionChoiceStateData)choice.State, Strength = (S.StructuralGuidanceStrength)choice.Strength,
                Applicability = choice.Applicability, Verification = (S.StructuralVerification)choice.Verification };
            result.KnowledgeClass.Values.Add(choice.Values.Select(v => new P.KnowledgeClassReferenceData
                { LibraryId = Id(v.LibraryId), LibraryRevision = v.LibraryRevision, ClassId = Id(v.ClassId) }));
            result.KnowledgeClass.Sources.Add(choice.Sources.Select(Source));
            if (choice.UnknownReason is { } reason) result.KnowledgeClass.UnknownReason = reason;
        }
        return result;
    }

    public static M.BlockDefinition Decode(P.BlockDefinitionData data)
    {
        Known(data, P.BlockDefinitionData.Parser);
        M.DefinitionChoice<M.KnowledgeClassReference>? knowledge = data.KnowledgeClass is { } choice
            ? new((M.DefinitionChoiceState)choice.State, choice.Values.Select(v => new M.KnowledgeClassReference(
                GuidValue(v.LibraryId), v.LibraryRevision, GuidValue(v.ClassId))).ToImmutableArray(),
                (M.GuidanceStrength)choice.Strength, choice.Applicability, choice.Sources.Select(Source).ToImmutableArray(),
                (M.VerificationState)choice.Verification, choice.HasUnknownReason ? choice.UnknownReason : null) : null;
        var definition = new M.BlockDefinition(DecodeChoice(data.Purpose), DecodeChoice(data.Type), DecodeChoice(data.Manufacturer),
            DecodeChoice(data.Family), DecodeChoice(data.Model), DecodeChoice(data.OrderablePart), DecodeChoice(data.Package), knowledge);
        definition.Validate(); return definition;
    }

    private static P.DefinitionTextChoiceData? EncodeChoice(M.DefinitionChoice<string>? choice)
    {
        if (choice is null) return null;
        var result = new P.DefinitionTextChoiceData { State = (P.DefinitionChoiceStateData)choice.State, Strength = (S.StructuralGuidanceStrength)choice.Strength,
            Applicability = choice.Applicability, Verification = (S.StructuralVerification)choice.Verification };
        result.Values.Add(choice.Values); result.Sources.Add(choice.Sources.Select(Source));
        if (choice.UnknownReason is { } reason) result.UnknownReason = reason;
        return result;
    }
    private static M.DefinitionChoice<string>? DecodeChoice(P.DefinitionTextChoiceData? data) => data is null ? null : new(
        (M.DefinitionChoiceState)data.State, data.Values.ToImmutableArray(), (M.GuidanceStrength)data.Strength, data.Applicability,
        data.Sources.Select(Source).ToImmutableArray(), (M.VerificationState)data.Verification, data.HasUnknownReason ? data.UnknownReason : null);

    public static M.RecursiveBlockDraft Decode(P.BlockDraftData data, Guid documentId)
    {
        Known(data, P.BlockDraftData.Parser);
        var baseline = Selection(Need(data.Baseline));
        var scope = new M.DiagramRequirementScope(documentId, baseline.BlockId, baseline.StateId);
        var restored = ImmutableDictionary.CreateBuilder<M.DiagramRequirementField, Guid>();
        foreach (var field in data.RestoredFields)
            if (!System.Enum.IsDefined((M.DiagramRequirementField)((int)field.Field - 1))
                || !restored.TryAdd((M.DiagramRequirementField)((int)field.Field - 1), GuidValue(field.SourceRevisionId)))
                throw Invalid("Field restorations need distinct supported categories and exact source revisions.");
        return new(baseline, data.Name, data.Children.Select(Selection).ToImmutableArray(),
            new(new(scope, GuidValue(data.BaselineRequirementRevisionId), Fields(Need(data.BaselineFields))),
                Fields(Need(data.Fields)), restored.ToImmutable()), data.RestoredFrom is { } source ? Selection(source) : null,
            data.LocalDiagram is { } diagram ? Local(diagram) : null,
            data.Definition is { } definition ? Decode(definition) : null,
            data.ComponentBindings is { } bindings ? Decode(bindings) : null,
            data.PhysicalAllocation is { } allocation ? Decode(allocation) : null);
    }

    public static P.BlockDraftData Encode(M.RecursiveBlockDraft draft)
    {
        var result = new P.BlockDraftData { Baseline = Selection(draft.Baseline), Name = draft.Name,
            BaselineRequirementRevisionId = Id(draft.Requirements.Baseline.RevisionId),
            BaselineFields = Fields(draft.Requirements.Baseline.Requirements), Fields = Fields(draft.Requirements.Requirements) };
        result.Children.Add(draft.Children.Select(Selection));
        result.RestoredFields.Add(draft.Requirements.RestoredFields.Select(r => new P.FieldRestorationData
            { Field = (P.RequirementFieldKind)((int)r.Key + 1), SourceRevisionId = Id(r.Value) }));
        if (draft.RestoredFrom is { } source) result.RestoredFrom = Selection(source);
        if (draft.Diagram is { } diagram) result.LocalDiagram = Local(diagram);
        if (draft.Definition is { } definition) result.Definition = Encode(definition);
        if (draft.ComponentBindings is { } bindings) result.ComponentBindings = Encode(bindings);
        if (draft.PhysicalAllocation is { } allocation) result.PhysicalAllocation = Encode(allocation);
        return result;
    }

    public static P.BlockComponentBindingsData Encode(M.BlockComponentBindings bindings)
    {
        bindings.Validate(); var data = new P.BlockComponentBindingsData();
        data.Targets.Add(bindings.Targets.Select(t => new P.ComponentRealizationData
            { DesignId = Id(t.DesignId), CircuitId = Id(t.CircuitId), ComponentId = Id(t.ComponentId) }));
        return data;
    }

    public static M.BlockComponentBindings Decode(P.BlockComponentBindingsData data)
    {
        Known(data, P.BlockComponentBindingsData.Parser);
        var bindings = new M.BlockComponentBindings(data.Targets.Select(t => new M.ComponentRealization(
            GuidValue(t.DesignId), GuidValue(t.CircuitId), GuidValue(t.ComponentId))).ToImmutableArray());
        bindings.Validate(); return bindings;
    }

    public static P.BlockPhysicalAllocationData Encode(M.BlockPhysicalAllocation allocation)
    {
        allocation.Validate();
        var data = new P.BlockPhysicalAllocationData { State = (P.PhysicalAllocationStateData) allocation.State };
        foreach (var target in allocation.Targets)
        {
            var row = new P.PhysicalAllocationTargetData
            { Id = Id(target.Id), Kind = (P.PhysicalAllocationKindData) ((int) target.Kind + 1), Name = target.Name };
            if (target.Reference is { } reference) row.Reference = reference;
            if (target.RepositoryPath is { } path) row.RepositoryPath = path;
            if (target.ParentId is { } parent) row.ParentId = Id(parent);
            data.Targets.Add(row);
        }
        if (allocation.UnknownReason is { } reason) data.UnknownReason = reason;
        return data;
    }

    public static M.BlockPhysicalAllocation Decode(P.BlockPhysicalAllocationData data)
    {
        Known(data, P.BlockPhysicalAllocationData.Parser);
        var allocation = new M.BlockPhysicalAllocation((M.PhysicalAllocationState) data.State,
            data.Targets.Select(t => new M.PhysicalAllocationTarget(GuidValue(t.Id),
                (M.PhysicalAllocationKind) ((int) t.Kind - 1), t.Name, t.HasReference ? t.Reference : null,
                t.HasRepositoryPath ? t.RepositoryPath : null, t.HasParentId ? GuidValue(t.ParentId) : null)).ToImmutableArray(),
            data.HasUnknownReason ? data.UnknownReason : null);
        allocation.Validate(); return allocation;
    }

    internal static M.RequirementRevisionOrigin DecodeOrigin(P.DiagramRevisionOriginData origin) => Origin(Need(origin));
    internal static M.ConnectionSelection DecodeSelection(P.ConnectionSelectionData selection) => Selection(Need(selection));
    internal static M.BlockSelection DecodeSelection(P.BlockSelectionData selection) => Selection(Need(selection));
    internal static Guid DecodeIdentity(string value) => GuidValue(value);
    private static M.DiagramRequirements Fields(P.RequirementFieldsData data) => new(data.General, data.Schematic, data.Routing);
    private static P.RequirementFieldsData Fields(M.DiagramRequirements fields) => new() { General = fields.General, Schematic = fields.Schematic, Routing = fields.Routing };

    public static M.DiagramConnectionDraft Decode(P.ConnectionDraftData data, Guid documentId)
    {
        Known(data, P.ConnectionDraftData.Parser); var baseline = Selection(Need(data.Baseline));
        var restored = ImmutableDictionary.CreateBuilder<M.DiagramRequirementField, Guid>();
        foreach (var field in data.RestoredFields)
            if (!System.Enum.IsDefined((M.DiagramRequirementField)((int)field.Field - 1))
                || !restored.TryAdd((M.DiagramRequirementField)((int)field.Field - 1), GuidValue(field.SourceRevisionId)))
                throw Invalid("Connection field restorations require distinct supported categories and exact source revisions.");
        return new(baseline, data.Name, (M.DiagramConnectionKind)((int)data.Kind - 1), data.Endpoints.Select(Decode).ToImmutableArray(),
            data.Members.Select(Selection).ToImmutableArray(), new(new(new(documentId, baseline.ConnectionId, baseline.StateId),
                GuidValue(data.BaselineRequirementRevisionId), Fields(Need(data.BaselineFields))), Fields(Need(data.Fields)), restored.ToImmutable()),
            data.DiagramAnnotations is { } notes ? notes.Annotations.Select(Note).ToImmutableArray() : default);
    }
    public static P.ConnectionDraftData Encode(M.DiagramConnectionDraft draft)
    {
        var result = new P.ConnectionDraftData { Baseline = Selection(draft.Baseline), Name = draft.Name,
            Kind = (P.DiagramConnectionKind)((int)draft.Kind + 1), BaselineRequirementRevisionId = Id(draft.Requirements.Baseline.RevisionId),
            BaselineFields = Fields(draft.Requirements.Baseline.Requirements), Fields = Fields(draft.Requirements.Requirements) };
        result.Endpoints.Add(draft.Endpoints.Select(Encode)); result.Members.Add(draft.Members.Select(Selection));
        result.RestoredFields.Add(draft.Requirements.RestoredFields.Select(r => new P.FieldRestorationData
            { Field = (P.RequirementFieldKind)((int)r.Key + 1), SourceRevisionId = Id(r.Value) }));
        if (!draft.DiagramAnnotations.IsDefault)
        { result.DiagramAnnotations = new(); result.DiagramAnnotations.Annotations.Add(draft.DiagramAnnotations.Select(Note)); }
        return result;
    }

    public static P.DiagramHistoryPageData Encode(M.DiagramHistoryPage page)
    {
        var result = new P.DiagramHistoryPageData { DocumentId = Id(page.DocumentId), Context = Selection(page.Context),
            ContextVersion = checked((uint)page.ContextVersion), Offset = checked((uint)page.Offset), Total = checked((uint)page.Total) };
        result.Entries.Add(page.Entries.Select(e => new P.DiagramHistoryEntryData { Selection = Selection(e.Selection), Version = checked((uint)e.Version),
            Name = e.Name, Origin = Origin(e.Origin), ChildCount = checked((uint)e.ChildCount), ConnectionCount = checked((uint)e.ConnectionCount),
            AnnotationCount = checked((uint)e.AnnotationCount), IsContext = e.IsContext }));
        return result;
    }

    public static P.DiagramHistoryComparisonData Encode(M.DiagramHistoryComparison comparison)
    {
        var result = new P.DiagramHistoryComparisonData { DocumentId = Id(comparison.DocumentId), Context = Selection(comparison.Context),
            Inspected = Selection(comparison.Inspected), ContextVersion = checked((uint)comparison.ContextVersion),
            InspectedVersion = checked((uint)comparison.InspectedVersion), InspectedOrigin = Origin(comparison.InspectedOrigin) };
        result.Changes.Add(comparison.Changes.Select(c => new P.DiagramHistoryChangeData { Category = (P.DiagramChangeCategory)((int)c.Category + 1),
            Kind = (P.DiagramChangeKind)((int)c.Kind + 1), ObjectId = Id(c.ObjectId), Name = c.Name,
            Field = c.Field is { } field ? (P.RequirementFieldKind)((int)field + 1) : P.RequirementFieldKind.RfkUnknown }));
        return result;
    }

    public static P.FieldHistoryPageData Encode(M.DiagramFieldHistoryPage page)
    {
        var result = new P.FieldHistoryPageData { DocumentId = Id(page.Scope.DocumentId), OwnerId = Id(page.Scope.OwnerId), StateId = Id(page.Scope.DesignStateId),
            Field = (P.RequirementFieldKind)((int)page.Field + 1), ContextRevisionId = Id(page.ContextRevisionId), ContextVersion = checked((uint)page.ContextVersion),
            RequirementRevisionId = Id(page.RequirementRevisionId), SavedText = page.SavedText, Offset = checked((uint)page.Offset), Total = checked((uint)page.Total) };
        result.Entries.Add(page.Entries.Select(e => new P.FieldHistoryEntryData { RequirementRevisionId = Id(e.RequirementRevisionId),
            ContextRevisionId = Id(e.ContextRevisionId), ContextVersion = checked((uint)e.ContextVersion), OwnerName = e.OwnerName, Text = e.Text,
            Origin = Origin(e.Origin), IsSavedText = e.IsSavedText }));
        return result;
    }

    public static P.RecursiveBlockGraphData Encode(M.RecursiveBlockGraph graph)
    {
        var data = new P.RecursiveBlockGraphData { SchemaVersion = 1, DocumentId = Id(graph.DocumentId), SelectedRoot = Selection(graph.SelectedRoot) };
        data.States.Add(graph.States.Select(s =>
        {
            var state = new P.BlockDesignStateData { Id = Id(s.Id), BlockId = Id(s.BlockId), Name = s.Name, HeadRevisionId = Id(s.HeadRevisionId), Archived = s.Archived };
            if (s.ForkedFrom is { } source) state.ForkedFrom = Selection(source);
            return state;
        }));
        foreach (var r in graph.Revisions)
        {
            var row = new P.BlockRevisionData { Selection = Selection(r.Selection), Name = r.Name,
                RequirementRevisionId = Id(r.RequirementRevisionId), Origin = Origin(r.Origin) };
            if (r.ParentRevisionId is { } parent) row.ParentRevisionId = Id(parent);
            if (r.RestoredFrom is { } restored) row.RestoredFrom = Selection(restored);
            if (r.Diagram is { } diagram) row.LocalDiagram = Local(diagram);
            if (r.Definition is { } definition) row.Definition = Encode(definition);
            if (r.ComponentBindings is { } bindings) row.ComponentBindings = Encode(bindings);
            if (r.PhysicalAllocation is { } allocation) row.PhysicalAllocation = Encode(allocation);
            row.Children.Add(r.Children.Select(Selection)); data.Revisions.Add(row);
        }
        data.RequirementHistories.Add(graph.RequirementHistories.Select(History));
        data.ConnectionArchives.Add(graph.ConnectionArchives.Select(Encode));
        data.ImplementationChanges.Add(graph.ImplementationChanges.Select(c => new P.ImplementationChangeData { Id = Id(c.Id), StateId = Id(c.StateId),
            Kind = (P.ImplementationChangeKind)((int)c.Kind + 1), BeforeName = c.BeforeName, AfterName = c.AfterName,
            BeforeArchived = c.BeforeArchived, AfterArchived = c.AfterArchived, Origin = Origin(c.Origin) }));
        data.RefinementInputs.Add(graph.RefinementInputs.Select(Encode));
        data.Proposals.Add(graph.Proposals.Select(Encode));
        return data;
    }

    public static M.RecursiveBlockGraph Decode(P.RecursiveBlockGraphData data)
    {
        Known(data, P.RecursiveBlockGraphData.Parser);
        if (data.SchemaVersion != 1) throw Invalid("Use the supported recursive diagram message version.");
        return new(GuidValue(data.DocumentId), Selection(Need(data.SelectedRoot)),
            data.States.Select(s => new M.BlockDesignState(GuidValue(s.Id), GuidValue(s.BlockId), s.Name, GuidValue(s.HeadRevisionId),
                s.ForkedFrom is { } source ? Selection(source) : null, s.Archived)),
            data.Revisions.Select(r => new M.RecursiveBlockRevision(Selection(Need(r.Selection)), r.HasParentRevisionId ? GuidValue(r.ParentRevisionId) : null,
                r.Name, GuidValue(r.RequirementRevisionId), r.Children.Select(Selection).ToImmutableArray(), Origin(Need(r.Origin)),
                r.RestoredFrom is { } source ? Selection(source) : null, r.LocalDiagram is { } diagram ? Local(diagram) : null,
                r.Definition is { } definition ? Decode(definition) : null,
                r.ComponentBindings is { } bindings ? Decode(bindings) : null,
                r.PhysicalAllocation is { } allocation ? Decode(allocation) : null)),
            data.RequirementHistories.Select(History), data.ConnectionArchives.Select(Decode),
            data.ImplementationChanges.Select(c => new M.ImplementationChange(GuidValue(c.Id), GuidValue(c.StateId),
                (M.ImplementationChangeKind)((int)c.Kind - 1), c.BeforeName, c.AfterName, c.BeforeArchived, c.AfterArchived, Origin(Need(c.Origin)))),
            data.RefinementInputs.Select(Decode), data.Proposals.Select(Decode));
    }

    public static P.BlockProposalRecordData Encode(M.BlockProposalRecord proposal)
    {
        var data = new P.BlockProposalRecordData { Id = Id(proposal.Id), InputId = Id(proposal.InputId), RequestSha256 = proposal.RequestSha256,
            Candidate = Selection(proposal.Candidate), Origin = Origin(proposal.Origin) };
        data.BasePath.Add(proposal.BasePath.Select(Selection));
        foreach (var issue in proposal.Issues)
        {
            var item = new P.BlockProposalIssueData { Id = Id(issue.Id), Kind = (P.BlockProposalIssueKindData)issue.Kind, Message = issue.Message };
            if (issue.TargetId is { } target) item.TargetId = Id(target);
            item.Sources.Add(issue.Sources.Select(Source)); data.Issues.Add(item);
        }
        return data;
    }

    public static M.BlockProposalRecord Decode(P.BlockProposalRecordData data)
    {
        Known(data, P.BlockProposalRecordData.Parser);
        return new(GuidValue(data.Id), GuidValue(data.InputId), data.RequestSha256, data.BasePath.Select(Selection).ToImmutableArray(),
            Selection(Need(data.Candidate)), data.Issues.Select(i => new M.BlockProposalIssue(GuidValue(i.Id), (M.BlockProposalIssueKind)i.Kind,
                i.Message, i.HasTargetId ? GuidValue(i.TargetId) : null, i.Sources.Select(Source).ToImmutableArray())).ToImmutableArray(), Origin(Need(data.Origin)));
    }

    public static P.DiagramRefinementInputData Encode(M.DiagramRefinementInput input)
    {
        input.Validate();
        var data = new P.DiagramRefinementInputData { Id = Id(input.Id), DocumentId = Id(input.DocumentId),
            SourceSha256 = input.SourceSha256, Prompt = input.Prompt, Origin = Origin(input.Origin) };
        data.BlockPath.Add(input.BlockPath.Select(Selection)); data.ConnectionPath.Add(input.ConnectionPath.Select(Selection));
        data.Attachments.Add(input.Attachments.Select(a =>
        {
            var item = new P.DiagramRefinementAttachmentData { Id = Id(a.Id), OriginalName = a.OriginalName,
                AssetPath = a.AssetPath, ContentSha256 = a.ContentSha256, ByteCount = a.ByteCount, MediaType = a.MediaType };
            if (a.Source is { } source) item.Source = Source(source);
            return item;
        }));
        return data;
    }

    public static M.DiagramRefinementInput Decode(P.DiagramRefinementInputData data)
    {
        Known(data, P.DiagramRefinementInputData.Parser);
        var input = new M.DiagramRefinementInput(GuidValue(data.Id), GuidValue(data.DocumentId), data.SourceSha256,
            data.BlockPath.Select(Selection).ToImmutableArray(), data.ConnectionPath.Select(Selection).ToImmutableArray(),
            data.Prompt, Origin(Need(data.Origin)), data.Attachments.Select(a => new M.DiagramRefinementAttachment(GuidValue(a.Id),
                a.OriginalName, a.AssetPath, a.ContentSha256, a.ByteCount, a.MediaType, a.Source is { } source ? Source(source) : null)).ToImmutableArray());
        input.Validate(); return input;
    }

    public static P.ConnectionArchiveData Encode(M.DiagramConnectionArchive archive)
    {
        var data = new P.ConnectionArchiveData { DocumentId = Id(archive.DocumentId), OwnerBlockId = Id(archive.OwnerBlockId) };
        data.States.Add(archive.States.Select(s => new P.ConnectionDesignStateData
            { Id = Id(s.Id), ConnectionId = Id(s.ConnectionId), Name = s.Name, HeadRevisionId = Id(s.HeadRevisionId) }));
        foreach (var r in archive.Revisions)
        {
            var row = new P.ConnectionRevisionData { Selection = Selection(r.Selection), Name = r.Name,
                Kind = (P.DiagramConnectionKind)((int)r.Kind + 1), RequirementRevisionId = Id(r.RequirementRevisionId), Origin = Origin(r.Origin) };
            if (r.ParentRevisionId is { } parent) row.ParentRevisionId = Id(parent);
            row.Endpoints.Add(r.Endpoints.Select(Encode)); row.Members.Add(r.Members.Select(Selection)); data.Revisions.Add(row);
        }
        data.RequirementHistories.Add(archive.RequirementHistories.Select(History)); return data;
    }

    public static M.DiagramConnectionArchive Decode(P.ConnectionArchiveData data)
    {
        Known(data, P.ConnectionArchiveData.Parser);
        return new(GuidValue(data.DocumentId), GuidValue(data.OwnerBlockId),
            data.States.Select(s => new M.ConnectionDesignState(GuidValue(s.Id), GuidValue(s.ConnectionId), s.Name, GuidValue(s.HeadRevisionId))),
            data.Revisions.Select(r => new M.DiagramConnectionRevision(Selection(Need(r.Selection)), r.HasParentRevisionId ? GuidValue(r.ParentRevisionId) : null,
                r.Name, (M.DiagramConnectionKind)((int)r.Kind - 1), r.Endpoints.Select(Decode).ToImmutableArray(), GuidValue(r.RequirementRevisionId),
                r.Members.Select(Selection).ToImmutableArray(), Origin(Need(r.Origin)))), data.RequirementHistories.Select(History));
    }

    private static P.RequirementHistoryData History(M.DiagramRequirementHistory history)
    {
        var row = new P.RequirementHistoryData { DocumentId = Id(history.Scope.DocumentId), OwnerId = Id(history.Scope.OwnerId), StateId = Id(history.Scope.DesignStateId) };
        foreach (var r in history.Revisions)
        {
            var revision = new P.RequirementRevisionData { Id = Id(r.Id), Origin = Origin(r.Origin),
                Fields = new() { General = r.Requirements.General, Schematic = r.Requirements.Schematic, Routing = r.Requirements.Routing } };
            if (r.ParentId is { } parent) revision.ParentId = Id(parent);
            revision.Restorations.Add(r.Restorations.Select(s => new P.FieldRestorationData
                { Field = (P.RequirementFieldKind)((int)s.Field + 1), SourceRevisionId = Id(s.SourceRevisionId) }));
            row.Revisions.Add(revision);
        }
        return row;
    }
    private static M.DiagramRequirementHistory History(P.RequirementHistoryData h) => new(new(GuidValue(h.DocumentId), GuidValue(h.OwnerId), GuidValue(h.StateId)),
        h.Revisions.Select(r => new M.DiagramRequirementRevision(GuidValue(r.Id), r.HasParentId ? GuidValue(r.ParentId) : null,
            new(Need(r.Fields).General, r.Fields.Schematic, r.Fields.Routing), Origin(Need(r.Origin)),
            r.Restorations.Select(s => new M.RequirementFieldRestoration((M.DiagramRequirementField)((int)s.Field - 1), GuidValue(s.SourceRevisionId))).ToImmutableArray())));
    private static P.BlockLocalDiagramData Local(M.BlockLocalDiagram diagram)
    {
        var result = new P.BlockLocalDiagramData();
        result.Interfaces.Add(diagram.Interfaces.Select(i => new P.DiagramBoundaryInterfaceData { Id = Id(i.Id), Name = i.Name, Intent = i.Intent }));
        result.Connections.Add(diagram.Connections.Select(Selection)); result.Annotations.Add(diagram.Notes.Select(Note)); return result;
    }
    private static M.BlockLocalDiagram Local(P.BlockLocalDiagramData diagram) => new(
        diagram.Interfaces.Select(i => new M.DiagramBoundaryInterface(GuidValue(i.Id), i.Name, i.Intent)).ToImmutableArray(),
        diagram.Connections.Select(Selection).ToImmutableArray(), diagram.Annotations.Select(Note).ToImmutableArray());
    private static P.DiagramAnnotationData Note(M.DiagramAnnotation note)
    {
        var result = new P.DiagramAnnotationData { Id = Id(note.Id), Role = (P.DiagramAnnotationRole)((int)note.Role + 1), Text = note.Text,
            TargetKind = (P.DiagramAnnotationTargetKind)((int)note.Target.Kind + 1), Origin = Origin(note.Origin), Units = "diagram-unit" };
        if (note.Target.TargetId is { } target) result.TargetId = Id(target);
        if (note.Target.UnresolvedReason is { } reason) result.UnresolvedReason = reason;
        if (note.Position is { } position) result.Position = Point(position);
        foreach (var stroke in note.Strokes) { var row = new P.DiagramAnnotationStrokeData(); row.Points.Add(stroke.Points.Select(Point)); result.Strokes.Add(row); }
        return result;
    }
    private static M.DiagramAnnotation Note(P.DiagramAnnotationData note)
    {
        if (note.Units != "diagram-unit") throw Invalid("Annotation positions and sketches must use diagram-unit presentation coordinates.");
        return new(GuidValue(note.Id), (M.DiagramAnnotationRole)((int)note.Role - 1), note.Text,
            new((M.DiagramAnnotationTargetKind)((int)note.TargetKind - 1), note.HasTargetId ? GuidValue(note.TargetId) : null,
                note.HasUnresolvedReason ? note.UnresolvedReason : null), note.Position is { } position ? Point(position) : null,
            note.Strokes.Select(s => new M.DiagramAnnotationStroke(s.Points.Select(Point).ToImmutableArray())).ToImmutableArray(), Origin(Need(note.Origin)));
    }
    private static P.DiagramAnnotationPointData Point(M.DiagramAnnotationPoint point) => new()
        { X = point.X.ToString(CultureInfo.InvariantCulture), Y = point.Y.ToString(CultureInfo.InvariantCulture) };
    private static M.DiagramAnnotationPoint Point(P.DiagramAnnotationPointData point) => M.DiagramAnnotationPoint.Parse(point.X, point.Y);
    private static P.ConnectionSelectionData Selection(M.ConnectionSelection s) => new() { ConnectionId = Id(s.ConnectionId), StateId = Id(s.StateId), RevisionId = Id(s.RevisionId) };
    private static M.ConnectionSelection Selection(P.ConnectionSelectionData s) => new(GuidValue(s.ConnectionId), GuidValue(s.StateId), GuidValue(s.RevisionId));

    public static P.DiagramEndpointBindingData Encode(M.DiagramEndpointBinding endpoint)
    {
        endpoint.Validate();
        var data = new P.DiagramEndpointBindingData { Kind = (P.DiagramEndpointKind)((int)endpoint.Kind + 1), BlockId = Id(endpoint.BlockId), Intent = endpoint.Intent };
        if (endpoint.InterfaceId is { } id) data.InterfaceId = Id(id);
        if (endpoint.Pin is { } pin) data.Pin = Pin(pin);
        if (endpoint.Selector is { } selector)
        {
            data.Selector = new() { Role = selector.Role, Protocol = selector.Protocol };
            data.Selector.RequiredFunctions.Add(selector.RequiredFunctions);
            data.Selector.Sources.Add(selector.Sources.Select(Source));
        }
        data.Candidates.Add(endpoint.Candidates.Select(Pin));
        return data;
    }

    public static M.DiagramEndpointBinding Decode(P.DiagramEndpointBindingData data)
    {
        Known(data, P.DiagramEndpointBindingData.Parser);
        var result = new M.DiagramEndpointBinding((M.DiagramEndpointKind)((int)data.Kind - 1), GuidValue(data.BlockId),
            data.HasInterfaceId ? GuidValue(data.InterfaceId) : null, data.Intent,
            data.Selector is { } selector ? new(selector.Role, selector.Protocol, selector.RequiredFunctions.ToImmutableArray(), selector.Sources.Select(Source).ToImmutableArray()) : null,
            data.Candidates.Select(Pin).ToImmutableArray(), data.Pin is { } pin ? Pin(pin) : null);
        result.Validate(); return result;
    }

    private static P.BlockSelectionData Selection(M.BlockSelection s) => new() { BlockId = Id(s.BlockId), StateId = Id(s.StateId), RevisionId = Id(s.RevisionId) };
    private static M.BlockSelection Selection(P.BlockSelectionData s) => new(GuidValue(s.BlockId), GuidValue(s.StateId), GuidValue(s.RevisionId));
    private static P.DiagramRevisionOriginData Origin(M.RequirementRevisionOrigin origin)
    {
        var result = new P.DiagramRevisionOriginData { Kind = (P.DiagramActorKind)((int)origin.ActorKind + 1), Actor = origin.Actor,
            RecordedAt = Timestamp.FromDateTimeOffset(origin.RecordedAt), Summary = origin.Summary };
        result.Sources.Add(origin.Sources.Select(Source)); result.InputIds.Add(origin.InputIds.Select(Id)); return result;
    }
    private static M.RequirementRevisionOrigin Origin(P.DiagramRevisionOriginData origin)
    {
        var at = Need(origin.RecordedAt);
        if (at.Seconds < -62135596800L || at.Seconds > 253402300799L || at.Nanos < 0 || at.Nanos >= 1000000000 || at.Nanos % 100 != 0)
            throw Invalid("Revision timestamps must fit the supported UTC range and exact 100-nanosecond precision.");
        return new((M.RequirementRevisionActor)((int)origin.Kind - 1), origin.Actor, at.ToDateTimeOffset(), origin.Summary,
            origin.Sources.Select(Source).ToImmutableArray(), origin.InputIds.Select(GuidValue).ToImmutableArray());
    }
    private static P.DiagramPinTargetData Pin(M.DiagramPinTarget pin)
    {
        var result = new P.DiagramPinTargetData { DesignId = Id(pin.DesignId), ComponentId = Id(pin.ComponentId), Pin = pin.Pin };
        result.SheetInstancePath.Add(pin.SheetInstancePath.Select(Id)); return result;
    }
    private static M.DiagramPinTarget Pin(P.DiagramPinTargetData pin) => new(GuidValue(pin.DesignId), GuidValue(pin.ComponentId), pin.SheetInstancePath.Select(GuidValue).ToImmutableArray(), pin.Pin);
    private static S.StructuralSourceReference Source(M.SourceReference source)
    {
        var result = new S.StructuralSourceReference { DocumentId = source.DocumentId, Revision = source.Revision };
        if (source.Page is { } page) result.Page = page;
        if (source.Table is { } table) result.Table = table;
        if (source.PartVariant is { } variant) result.PartVariant = variant;
        return result;
    }
    private static M.SourceReference Source(S.StructuralSourceReference source) => new(source.DocumentId, source.Revision,
        source.HasPage ? source.Page : null, source.HasTable ? source.Table : null, source.HasPartVariant ? source.PartVariant : null);

    // Schema 2 fields declared at the Phase 2 freeze (contract rbg-v2) that this version neither
    // reads nor writes. Until lane 2B implements each one and removes it here, a set value is refused
    // as the unsupported field it was before the declaration, and observations leave it out, so no
    // history is silently simplified and no computed default is reported as a measured fact.
    private static readonly HashSet<FieldDescriptor> Unimplemented =
    [
        .. Declared(P.DiagramBoundaryInterfaceData.Descriptor,
            P.DiagramBoundaryInterfaceData.DomainFieldNumber, P.DiagramBoundaryInterfaceData.DirectionFieldNumber),
        .. Declared(P.BlockLocalDiagramData.Descriptor,
            P.BlockLocalDiagramData.PresentationFieldNumber, P.BlockLocalDiagramData.InterfaceRealizationsFieldNumber),
        .. Declared(P.ConnectionDraftData.Descriptor, P.ConnectionDraftData.DomainFieldNumber,
            P.ConnectionDraftData.DirectionFieldNumber, P.ConnectionDraftData.RealizationFieldNumber),
        .. Declared(P.ConnectionRevisionData.Descriptor, P.ConnectionRevisionData.DomainFieldNumber,
            P.ConnectionRevisionData.DirectionFieldNumber, P.ConnectionRevisionData.RealizationFieldNumber),
        .. Declared(P.RecursiveBlockGraphData.Descriptor, P.RecursiveBlockGraphData.MigrationFieldNumber),
        .. Declared(P.RecursiveEditorDocument.Descriptor,
            P.RecursiveEditorDocument.StoredSchemaVersionFieldNumber, P.RecursiveEditorDocument.SourceWritableFieldNumber),
        .. Declared(P.DiagramHistoryEntryData.Descriptor, P.DiagramHistoryEntryData.LayoutOnlyFieldNumber),
        .. Declared(P.RecursiveDiagramEditorState.Descriptor, P.RecursiveDiagramEditorState.StoredSchemaVersionFieldNumber,
            P.RecursiveDiagramEditorState.SourceWritableFieldNumber, P.RecursiveDiagramEditorState.LevelDraftFieldNumber,
            P.RecursiveDiagramEditorState.LevelViewportsFieldNumber, P.RecursiveDiagramEditorState.CanvasToolFieldNumber,
            P.RecursiveDiagramEditorState.SelectedInterfaceIdFieldNumber),
        .. Declared(P.RecursiveDiagramView.Descriptor, P.RecursiveDiagramView.ResolvedLayoutFieldNumber),
    ];

    private static IEnumerable<FieldDescriptor> Declared(MessageDescriptor message, params int[] numbers) => numbers.Select(number =>
        message.FindFieldByNumber(number) ?? throw new InvalidOperationException($"{message.FullName} does not declare field {number}."));

    /// <summary>True when a declared schema 2 field this version does not implement carries a value
    /// anywhere in the message tree.</summary>
    public static bool CarriesUnimplementedField(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            object? value = field.Accessor.GetValue(message);
            if (Unimplemented.Contains(field) && IsSet(message, field, value)) return true;
            if (field.FieldType != FieldType.Message) continue;
            if (value is System.Collections.IDictionary map)
            {
                foreach (object? item in map.Values)
                    if (item is IMessage child && CarriesUnimplementedField(child)) return true;
            }
            else if (value is System.Collections.IList list)
            {
                foreach (object? item in list)
                    if (item is IMessage child && CarriesUnimplementedField(child)) return true;
            }
            else if (value is IMessage child && CarriesUnimplementedField(child)) return true;
        }
        return false;
    }

    /// <summary>Removes the declared schema 2 fields this version does not implement from the protobuf
    /// JSON rendering of <paramref name="message"/>, so an observation keeps its earlier shape.</summary>
    public static void OmitUnimplementedFields(IMessage message, System.Text.Json.Nodes.JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(message); ArgumentNullException.ThrowIfNull(json);
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (Unimplemented.Contains(field)) { json.Remove(field.JsonName); continue; }
            if (field.FieldType != FieldType.Message || field.IsMap || json[field.JsonName] is not { } node) continue;
            object? value = field.Accessor.GetValue(message);
            if (field.IsRepeated && value is System.Collections.IList list && node is System.Text.Json.Nodes.JsonArray rows)
            {
                for (int i = 0; i < Math.Min(list.Count, rows.Count); ++i)
                    if (list[i] is IMessage child && rows[i] is System.Text.Json.Nodes.JsonObject row) OmitUnimplementedFields(child, row);
            }
            else if (!field.IsRepeated && value is IMessage child && node is System.Text.Json.Nodes.JsonObject nested)
                OmitUnimplementedFields(child, nested);
        }
    }

    private static bool IsSet(IMessage message, FieldDescriptor field, object? value) => field.IsRepeated || field.IsMap
        ? value is System.Collections.ICollection { Count: > 0 }
        : field.HasPresence ? field.Accessor.HasValue(message) : value switch
        {
            null => false,
            string text => text.Length != 0,
            ByteString bytes => !bytes.IsEmpty,
            bool flag => flag,
            System.Enum choice => Convert.ToInt64(choice, CultureInfo.InvariantCulture) != 0,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0,
        };

    private static void Known<T>(T data, MessageParser<T> parser) where T : class, IMessage<T>
    {
        Need(data);
        try
        {
            if (!data.Equals(parser.ParseJson(JsonFormatter.Default.Format(data))) || CarriesUnimplementedField(data))
                throw Invalid("The recursive diagram message contains unsupported fields; no history was simplified.");
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidProtocolBufferException)
        { throw Invalid("The diagram message has an invalid protobuf value; no history was changed."); }
    }
    private static T Need<T>(T? value) where T : class => value ?? throw Invalid("A required recursive diagram record is missing.");
    private static string Id(Guid id) => id.ToString("D");
    private static Guid GuidValue(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && Id(id) == value
        ? id : throw Invalid("Diagram identities require canonical non-empty UUIDs.");
    private static M.AutomationException Invalid(string message) => new("invalid_recursive_diagram_data", message);
}
