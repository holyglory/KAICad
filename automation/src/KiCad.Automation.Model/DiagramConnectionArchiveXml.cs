using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

public static class DiagramConnectionArchiveXml
{
    public const string Namespace = "urn:kicad:automation:connection-archive:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(DiagramConnectionArchiveXml).Assembly;
        foreach (string name in new[] { "requirement-history-v1.xsd", "connection-archive-v1.xsd" })
        {
            string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
        }
        set.Compile(); return set;
    });

    public static string Write(DiagramConnectionArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var root = new XElement(Ns + "connection-archive", new XAttribute("version", 1), Attr("document", archive.DocumentId), Attr("owner-block", archive.OwnerBlockId),
            new XElement(Ns + "states", archive.States.OrderBy(s => s.Id).Select(s => new XElement(Ns + "state", Attr("id", s.Id),
                Attr("connection", s.ConnectionId), new XAttribute("name", s.Name), Attr("head", s.HeadRevisionId)))),
            new XElement(Ns + "revisions", archive.Revisions.OrderBy(r => r.Selection.RevisionId).Select(r => new XElement(Ns + "revision",
                Attr("id", r.Selection.RevisionId), Attr("connection", r.Selection.ConnectionId), Attr("state", r.Selection.StateId),
                r.ParentRevisionId is { } parent ? Attr("parent", parent) : null, new XAttribute("name", r.Name), new XAttribute("kind", r.Kind),
                Attr("requirements", r.RequirementRevisionId), DiagramRevisionOriginXml.Write(Ns, r.Origin),
                new XElement(Ns + "endpoints", r.Endpoints.Select(WriteEndpoint)),
                new XElement(Ns + "members", r.Members.Select(m => new XElement(Ns + "member", Attr("connection", m.ConnectionId), Attr("state", m.StateId), Attr("revision", m.RevisionId))))))),
            new XElement(Ns + "requirement-histories", archive.RequirementHistories.OrderBy(h => h.Scope.DesignStateId)
                .Select(h => EngineeringXmlText.Parse(DiagramRequirementHistoryXml.Write(h)))));
        new XDocument(root).Validate(Schema.Value, null); return EngineeringXmlText.Render(root);
    }

    public static DiagramConnectionArchive Read(string xml)
    {
        if (xml is null) throw Invalid("Provide the typed connection archive XML.");
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "connection-archive") throw Invalid("Use the supported connection archive root and namespace.");
            new XDocument(root).Validate(Schema.Value, null);
            return new(Id(root, "document"), Id(root, "owner-block"),
                root.Element(Ns + "states")!.Elements(Ns + "state").Select(s => new ConnectionDesignState(Id(s, "id"), Id(s, "connection"), Text(s, "name"), Id(s, "head"))),
                root.Element(Ns + "revisions")!.Elements(Ns + "revision").Select(r => new DiagramConnectionRevision(
                    new(Id(r, "connection"), Id(r, "state"), Id(r, "id")), r.Attribute("parent") is null ? null : Id(r, "parent"),
                    Text(r, "name"), Enum.Parse<DiagramConnectionKind>(Text(r, "kind")),
                    r.Element(Ns + "endpoints")!.Elements(Ns + "endpoint").Select(ReadEndpoint).ToImmutableArray(), Id(r, "requirements"),
                    r.Element(Ns + "members")!.Elements(Ns + "member").Select(m => new ConnectionSelection(Id(m, "connection"), Id(m, "state"), Id(m, "revision"))).ToImmutableArray(),
                    DiagramRevisionOriginXml.Read(r.Element(Ns + "origin")!))),
                root.Element(Ns + "requirement-histories")!.Elements(XName.Get("requirement-history", DiagramRequirementHistoryXml.Namespace))
                    .Select(h => DiagramRequirementHistoryXml.Read(EngineeringXmlText.Render(h))));
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw Invalid("Invalid connection archive XML: " + error.Message); }
    }

    private static XElement WriteEndpoint(DiagramEndpointBinding endpoint) => new(Ns + "endpoint",
        new XAttribute("kind", endpoint.Kind), Attr("block", endpoint.BlockId), endpoint.InterfaceId is { } id ? Attr("interface", id) : null,
        new XElement(Ns + "intent", endpoint.Intent), endpoint.Selector is { } selector ? new XElement(Ns + "selector",
            new XAttribute("role", selector.Role), new XAttribute("protocol", selector.Protocol),
            selector.RequiredFunctions.Select(f => new XElement(Ns + "function", f)), selector.Sources.Select(s => DiagramRevisionOriginXml.Source(Ns, s))) : null,
        new XElement(Ns + "candidates", endpoint.Candidates.Select(p => WritePin("pin", p))),
        endpoint.Pin is { } pin ? WritePin("chosen-pin", pin) : null);

    private static DiagramEndpointBinding ReadEndpoint(XElement e) => new(Enum.Parse<DiagramEndpointKind>(Text(e, "kind")), Id(e, "block"),
        e.Attribute("interface") is null ? null : Id(e, "interface"), e.Element(Ns + "intent")!.Value,
        e.Element(Ns + "selector") is { } selector ? new(Text(selector, "role"), Text(selector, "protocol"),
            selector.Elements(Ns + "function").Select(f => f.Value).ToImmutableArray(),
            selector.Elements(Ns + "source").Select(DiagramRevisionOriginXml.ReadSource).ToImmutableArray()) : null,
        e.Element(Ns + "candidates")!.Elements(Ns + "pin").Select(ReadPin).ToImmutableArray(),
        e.Element(Ns + "chosen-pin") is { } pin ? ReadPin(pin) : null);

    private static XElement WritePin(string name, DiagramPinTarget pin) => new(Ns + name, Attr("design", pin.DesignId), Attr("component", pin.ComponentId),
        new XAttribute("pin", pin.Pin), pin.SheetInstancePath.Select(id => new XElement(Ns + "sheet", Attr("instance", id))));
    private static DiagramPinTarget ReadPin(XElement pin) => new(Id(pin, "design"), Id(pin, "component"),
        pin.Elements(Ns + "sheet").Select(s => Id(s, "instance")).ToImmutableArray(), Text(pin, "pin"));
    private static string Text(XElement e, string name) => e.Attribute(name)!.Value;
    private static Guid Id(XElement e, string name) => Guid.ParseExact(Text(e, name), "D");
    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
    private static AutomationException Invalid(string message) => new("invalid_connection_archive_xml", message);
}
