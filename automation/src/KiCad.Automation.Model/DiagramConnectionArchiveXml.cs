using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Typed connection archive storage. Reads schema 1 and 2 and writes schema 2 (contract
/// rbg-v2 section 5). The version 1 schema is frozen; a version 1 archive element is produced only
/// inside a version 1 graph written explicitly by <c>RecursiveBlockGraphXml.Write(graph, 1)</c>, which
/// only the frozen PSU-CPU fixture uses.</summary>
public static class DiagramConnectionArchiveXml
{
    public const string Namespace = "urn:kicad:automation:connection-archive:2";
    public const string NamespaceV1 = "urn:kicad:automation:connection-archive:1";
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(DiagramConnectionArchiveXml).Assembly;
        foreach (string name in new[] { "requirement-history-v1.xsd", "connection-archive-v1.xsd", "connection-archive-v2.xsd" })
        {
            string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
        }
        set.Compile(); return set;
    });

    /// <summary>1 unless a revision states a domain, direction or interconnect realization.</summary>
    public static int RequiredSchemaVersion(DiagramConnectionArchive archive) =>
        archive.Revisions.Any(r => r.UsesSchemaTwo) ? 2 : 1;

    public static string Write(DiagramConnectionArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return Write(archive, 2);
    }

    /// <summary>The archive in the namespace of its containing graph document.</summary>
    internal static string Write(DiagramConnectionArchive archive, int schemaVersion)
    {
        var root = Element(archive, schemaVersion);
        new XDocument(root).Validate(Schema.Value, null); return EngineeringXmlText.Render(root);
    }

    internal static XElement Element(DiagramConnectionArchive archive, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (schemaVersion is not (1 or 2) || schemaVersion < RequiredSchemaVersion(archive))
            throw new InvalidOperationException("A connection archive with schema 2 content cannot be written as schema 1.");
        XNamespace ns = schemaVersion == 2 ? Namespace : NamespaceV1;
        return new XElement(ns + "connection-archive", new XAttribute("version", schemaVersion), Attr("document", archive.DocumentId), Attr("owner-block", archive.OwnerBlockId),
            new XElement(ns + "states", archive.States.OrderBy(s => s.Id).Select(s => new XElement(ns + "state", Attr("id", s.Id),
                Attr("connection", s.ConnectionId), new XAttribute("name", s.Name), Attr("head", s.HeadRevisionId)))),
            new XElement(ns + "revisions", archive.Revisions.OrderBy(r => r.Selection.RevisionId).Select(r => new XElement(ns + "revision",
                Attr("id", r.Selection.RevisionId), Attr("connection", r.Selection.ConnectionId), Attr("state", r.Selection.StateId),
                r.ParentRevisionId is { } parent ? Attr("parent", parent) : null, new XAttribute("name", r.Name), new XAttribute("kind", r.Kind),
                Attr("requirements", r.RequirementRevisionId),
                r.Domain == DiagramDomain.Unspecified ? null : new XAttribute("domain", r.Domain),
                r.Direction == DiagramConnectionDirection.Unspecified ? null : new XAttribute("direction", r.Direction),
                DiagramRevisionOriginXml.Write(ns, r.Origin),
                new XElement(ns + "endpoints", r.Endpoints.Select(e => WriteEndpoint(ns, e))),
                new XElement(ns + "members", r.Members.Select(m => new XElement(ns + "member", Attr("connection", m.ConnectionId), Attr("state", m.StateId), Attr("revision", m.RevisionId)))),
                r.Realization is { } realization ? WriteRealization(ns, realization) : null))),
            new XElement(ns + "requirement-histories", archive.RequirementHistories.OrderBy(h => h.Scope.DesignStateId)
                .Select(h => EngineeringXmlText.Parse(DiagramRequirementHistoryXml.Write(h)))));
    }

    public static DiagramConnectionArchive Read(string xml)
    {
        if (xml is null) throw Invalid("Provide the typed connection archive XML.");
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            XNamespace ns = root.Name.Namespace;
            if (root.Name.LocalName != "connection-archive" || (ns != Namespace && ns != NamespaceV1))
                throw Invalid("Use the supported connection archive root and namespace.");
            new XDocument(root).Validate(Schema.Value, null);
            return new(Id(root, "document"), Id(root, "owner-block"),
                root.Element(ns + "states")!.Elements(ns + "state").Select(s => new ConnectionDesignState(Id(s, "id"), Id(s, "connection"), Text(s, "name"), Id(s, "head"))),
                root.Element(ns + "revisions")!.Elements(ns + "revision").Select(r => new DiagramConnectionRevision(
                    new(Id(r, "connection"), Id(r, "state"), Id(r, "id")), r.Attribute("parent") is null ? null : Id(r, "parent"),
                    Text(r, "name"), Enum.Parse<DiagramConnectionKind>(Text(r, "kind")),
                    r.Element(ns + "endpoints")!.Elements(ns + "endpoint").Select(e => ReadEndpoint(ns, e)).ToImmutableArray(), Id(r, "requirements"),
                    r.Element(ns + "members")!.Elements(ns + "member").Select(m => new ConnectionSelection(Id(m, "connection"), Id(m, "state"), Id(m, "revision"))).ToImmutableArray(),
                    DiagramRevisionOriginXml.Read(r.Element(ns + "origin")!),
                    r.Attribute("domain") is { } domain ? Enum.Parse<DiagramDomain>(domain.Value) : DiagramDomain.Unspecified,
                    r.Attribute("direction") is { } direction ? Enum.Parse<DiagramConnectionDirection>(direction.Value) : DiagramConnectionDirection.Unspecified,
                    r.Element(ns + "interconnect-realization") is { } realization ? ReadRealization(ns, realization) : null)),
                root.Element(ns + "requirement-histories")!.Elements(XName.Get("requirement-history", DiagramRequirementHistoryXml.Namespace))
                    .Select(h => DiagramRequirementHistoryXml.Read(EngineeringXmlText.Render(h))));
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw Invalid("Invalid connection archive XML: " + error.Message); }
    }

    private static XElement WriteRealization(XNamespace ns, InterconnectRealization realization) => new(ns + "interconnect-realization",
        new XAttribute("state", realization.State),
        realization.UnresolvedReason is { } reason ? new XElement(ns + "unresolved-reason", reason) : null,
        realization.SegmentList.Select(s => new XElement(ns + "segment", Attr("id", s.Id), new XAttribute("kind", s.Kind),
            s.Label is { } label ? new XAttribute("label", label) : null, Optional("design", s.DesignId), Optional("circuit", s.CircuitId),
            Optional("net", s.NetId), Optional("component", s.ComponentId), Optional("physical-target", s.PhysicalTargetId),
            Optional("hardware-interface", s.HardwareInterfaceId), s.Reference is { } reference ? new XAttribute("reference", reference) : null,
            s.RepositoryPath is { } path ? new XAttribute("repository-path", path) : null,
            s.PinList.Select(p => WritePin(ns, "pin", p)),
            s.UnresolvedReason is { } segmentReason ? new XElement(ns + "unresolved-reason", segmentReason) : null)),
        realization.JoinList.Select(j => new XElement(ns + "join", Attr("first", j.FirstSegmentId), Attr("second", j.SecondSegmentId))),
        realization.SourceList.Select(s => DiagramRevisionOriginXml.Source(ns, s)));

    private static InterconnectRealization ReadRealization(XNamespace ns, XElement e) => new(Enum.Parse<DiagramRealizationState>(Text(e, "state")),
        e.Elements(ns + "segment").Select(s => new InterconnectSegment(Id(s, "id"), Enum.Parse<InterconnectSegmentKind>(Text(s, "kind")),
            (string?)s.Attribute("label"), OptionalId(s, "design"), OptionalId(s, "circuit"), OptionalId(s, "net"), OptionalId(s, "component"),
            s.Elements(ns + "pin").Select(p => ReadPin(ns, p)).ToImmutableArray(), OptionalId(s, "physical-target"), OptionalId(s, "hardware-interface"),
            (string?)s.Attribute("reference"), (string?)s.Attribute("repository-path"), s.Element(ns + "unresolved-reason")?.Value)).ToImmutableArray(),
        e.Elements(ns + "join").Select(j => new InterconnectJoin(Id(j, "first"), Id(j, "second"))).ToImmutableArray(),
        e.Element(ns + "unresolved-reason")?.Value, e.Elements(ns + "source").Select(DiagramRevisionOriginXml.ReadSource).ToImmutableArray());

    private static XElement WriteEndpoint(XNamespace ns, DiagramEndpointBinding endpoint) => new(ns + "endpoint",
        new XAttribute("kind", endpoint.Kind), Attr("block", endpoint.BlockId), endpoint.InterfaceId is { } id ? Attr("interface", id) : null,
        new XElement(ns + "intent", endpoint.Intent), endpoint.Selector is { } selector ? new XElement(ns + "selector",
            new XAttribute("role", selector.Role), new XAttribute("protocol", selector.Protocol),
            selector.RequiredFunctions.Select(f => new XElement(ns + "function", f)), selector.Sources.Select(s => DiagramRevisionOriginXml.Source(ns, s))) : null,
        new XElement(ns + "candidates", endpoint.Candidates.Select(p => WritePin(ns, "pin", p))),
        endpoint.Pin is { } pin ? WritePin(ns, "chosen-pin", pin) : null);

    private static DiagramEndpointBinding ReadEndpoint(XNamespace ns, XElement e) => new(Enum.Parse<DiagramEndpointKind>(Text(e, "kind")), Id(e, "block"),
        e.Attribute("interface") is null ? null : Id(e, "interface"), e.Element(ns + "intent")!.Value,
        e.Element(ns + "selector") is { } selector ? new(Text(selector, "role"), Text(selector, "protocol"),
            selector.Elements(ns + "function").Select(f => f.Value).ToImmutableArray(),
            selector.Elements(ns + "source").Select(DiagramRevisionOriginXml.ReadSource).ToImmutableArray()) : null,
        e.Element(ns + "candidates")!.Elements(ns + "pin").Select(p => ReadPin(ns, p)).ToImmutableArray(),
        e.Element(ns + "chosen-pin") is { } pin ? ReadPin(ns, pin) : null);

    internal static XElement WritePin(XNamespace ns, string name, DiagramPinTarget pin) => new(ns + name, Attr("design", pin.DesignId), Attr("component", pin.ComponentId),
        new XAttribute("pin", pin.Pin), pin.SheetInstancePath.Select(id => new XElement(ns + "sheet", Attr("instance", id))));
    internal static DiagramPinTarget ReadPin(XNamespace ns, XElement pin) => new(Id(pin, "design"), Id(pin, "component"),
        pin.Elements(ns + "sheet").Select(s => Id(s, "instance")).ToImmutableArray(), Text(pin, "pin"));
    private static XAttribute? Optional(string name, Guid? value) => value is { } id ? Attr(name, id) : null;
    private static Guid? OptionalId(XElement e, string name) => e.Attribute(name) is null ? null : Id(e, name);
    private static string Text(XElement e, string name) => e.Attribute(name)!.Value;
    private static Guid Id(XElement e, string name) => Guid.ParseExact(Text(e, name), "D");
    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
    private static AutomationException Invalid(string message) => new("invalid_connection_archive_xml", message);
}
