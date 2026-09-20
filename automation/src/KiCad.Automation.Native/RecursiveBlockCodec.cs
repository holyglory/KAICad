using System.Collections.Immutable;
using System.Globalization;
using Google.Protobuf;
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
            data.Definition is { } definition ? Decode(definition) : null);
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
        return result;
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
            row.Children.Add(r.Children.Select(Selection)); data.Revisions.Add(row);
        }
        data.RequirementHistories.Add(graph.RequirementHistories.Select(History));
        data.ConnectionArchives.Add(graph.ConnectionArchives.Select(Encode));
        data.ImplementationChanges.Add(graph.ImplementationChanges.Select(c => new P.ImplementationChangeData { Id = Id(c.Id), StateId = Id(c.StateId),
            Kind = (P.ImplementationChangeKind)((int)c.Kind + 1), BeforeName = c.BeforeName, AfterName = c.AfterName,
            BeforeArchived = c.BeforeArchived, AfterArchived = c.AfterArchived, Origin = Origin(c.Origin) }));
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
                r.Definition is { } definition ? Decode(definition) : null)),
            data.RequirementHistories.Select(History), data.ConnectionArchives.Select(Decode),
            data.ImplementationChanges.Select(c => new M.ImplementationChange(GuidValue(c.Id), GuidValue(c.StateId),
                (M.ImplementationChangeKind)((int)c.Kind - 1), c.BeforeName, c.AfterName, c.BeforeArchived, c.AfterArchived, Origin(Need(c.Origin)))));
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
    private static void Known<T>(T data, MessageParser<T> parser) where T : class, IMessage<T>
    {
        Need(data);
        try
        {
            if (!data.Equals(parser.ParseJson(JsonFormatter.Default.Format(data))))
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
