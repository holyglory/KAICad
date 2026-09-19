using System.Collections.Immutable;
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
    public static P.RecursiveBlockGraphData Encode(M.RecursiveBlockGraph graph)
    {
        var data = new P.RecursiveBlockGraphData { SchemaVersion = 1, DocumentId = Id(graph.DocumentId), SelectedRoot = Selection(graph.SelectedRoot) };
        data.States.Add(graph.States.Select(s => new P.BlockDesignStateData
            { Id = Id(s.Id), BlockId = Id(s.BlockId), Name = s.Name, HeadRevisionId = Id(s.HeadRevisionId) }));
        foreach (var r in graph.Revisions)
        {
            var row = new P.BlockRevisionData { Selection = Selection(r.Selection), Name = r.Name,
                RequirementRevisionId = Id(r.RequirementRevisionId), Origin = Origin(r.Origin) };
            if (r.ParentRevisionId is { } parent) row.ParentRevisionId = Id(parent);
            if (r.RestoredFrom is { } restored) row.RestoredFrom = Selection(restored);
            row.Children.Add(r.Children.Select(Selection)); data.Revisions.Add(row);
        }
        foreach (var history in graph.RequirementHistories)
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
            data.RequirementHistories.Add(row);
        }
        return data;
    }

    public static M.RecursiveBlockGraph Decode(P.RecursiveBlockGraphData data)
    {
        Known(data, P.RecursiveBlockGraphData.Parser);
        if (data.SchemaVersion != 1) throw Invalid("Use the supported recursive diagram message version.");
        return new(GuidValue(data.DocumentId), Selection(Need(data.SelectedRoot)),
            data.States.Select(s => new M.BlockDesignState(GuidValue(s.Id), GuidValue(s.BlockId), s.Name, GuidValue(s.HeadRevisionId))),
            data.Revisions.Select(r => new M.RecursiveBlockRevision(Selection(Need(r.Selection)), r.HasParentRevisionId ? GuidValue(r.ParentRevisionId) : null,
                r.Name, GuidValue(r.RequirementRevisionId), r.Children.Select(Selection).ToImmutableArray(), Origin(Need(r.Origin)),
                r.RestoredFrom is { } source ? Selection(source) : null)),
            data.RequirementHistories.Select(h => new M.DiagramRequirementHistory(new(GuidValue(h.DocumentId), GuidValue(h.OwnerId), GuidValue(h.StateId)),
                h.Revisions.Select(r => new M.DiagramRequirementRevision(GuidValue(r.Id), r.HasParentId ? GuidValue(r.ParentId) : null,
                    new(Need(r.Fields).General, r.Fields.Schematic, r.Fields.Routing), Origin(Need(r.Origin)),
                    r.Restorations.Select(s => new M.RequirementFieldRestoration((M.DiagramRequirementField)((int)s.Field - 1), GuidValue(s.SourceRevisionId))).ToImmutableArray())))));
    }

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
    private static M.DiagramPinTarget Pin(P.DiagramPinTarget pin) => new(GuidValue(pin.DesignId), GuidValue(pin.ComponentId), pin.SheetInstancePath.Select(GuidValue).ToImmutableArray(), pin.Pin);
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
        if (!data.Equals(parser.ParseJson(JsonFormatter.Default.Format(data))))
            throw Invalid("The recursive diagram message contains unsupported fields; no history was simplified.");
    }
    private static T Need<T>(T? value) where T : class => value ?? throw Invalid("A required recursive diagram record is missing.");
    private static string Id(Guid id) => id.ToString("D");
    private static Guid GuidValue(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && Id(id) == value
        ? id : throw Invalid("Diagram identities require canonical non-empty UUIDs.");
    private static M.AutomationException Invalid(string message) => new("invalid_recursive_diagram_data", message);
}
