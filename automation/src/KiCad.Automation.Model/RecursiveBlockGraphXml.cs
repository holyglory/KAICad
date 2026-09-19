using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Typed revision-graph storage. Children pin exact revisions and requirement
/// histories are explicit XML fields, never an embedded opaque native schematic.</summary>
public static class RecursiveBlockGraphXml
{
    public const string Namespace = "urn:kicad:automation:recursive-block-graph:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(CreateSchemaSet);

    public static XmlSchemaSet CreateSchemaSet()
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(RecursiveBlockGraphXml).Assembly;
        foreach (string name in new[] { "requirement-history-v1.xsd", "recursive-block-graph-v1.xsd" })
        {
            string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
        }
        set.Compile(); return set;
    }

    public static string Write(RecursiveBlockGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var root = new XElement(Ns + "recursive-block-graph", new XAttribute("version", 1), Attr("document", graph.DocumentId),
            Selection("selected-root", graph.SelectedRoot),
            new XElement(Ns + "states", graph.States.OrderBy(s => s.Id).Select(s => new XElement(Ns + "state",
                Attr("id", s.Id), Attr("block", s.BlockId), new XAttribute("name", s.Name), Attr("head", s.HeadRevisionId)))),
            new XElement(Ns + "revisions", graph.Revisions.OrderBy(r => r.Selection.RevisionId).Select(r =>
                new XElement(Ns + "revision", Attr("id", r.Selection.RevisionId), Attr("block", r.Selection.BlockId), Attr("state", r.Selection.StateId),
                    r.ParentRevisionId is { } parent ? Attr("parent", parent) : null, new XAttribute("name", r.Name),
                    Attr("requirements", r.RequirementRevisionId), WriteOrigin(r.Origin),
                    new XElement(Ns + "children", r.Children.Select(c => Selection("child", c)))))),
            new XElement(Ns + "requirement-histories", graph.RequirementHistories.OrderBy(h => h.Scope.DesignStateId)
                .Select(h => EngineeringXmlText.Parse(DiagramRequirementHistoryXml.Write(h)))));
        new XDocument(root).Validate(Schema.Value, null);
        return EngineeringXmlText.Render(root);
    }

    public static RecursiveBlockGraph Read(string xml)
    {
        if (xml is null) throw Invalid("A recursive block graph document is required.");
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "recursive-block-graph") throw Invalid("Use the supported recursive block graph root and namespace.");
            new XDocument(root).Validate(Schema.Value, null);
            var states = root.Element(Ns + "states")!.Elements(Ns + "state").Select(s =>
                new BlockDesignState(Id(s, "id"), Id(s, "block"), Text(s, "name"), Id(s, "head")));
            var revisions = root.Element(Ns + "revisions")!.Elements(Ns + "revision").Select(r =>
                new RecursiveBlockRevision(new(Id(r, "block"), Id(r, "state"), Id(r, "id")),
                    r.Attribute("parent") is null ? null : Id(r, "parent"), Text(r, "name"), Id(r, "requirements"),
                    r.Element(Ns + "children")!.Elements(Ns + "child").Select(ReadSelection).ToImmutableArray(),
                    ReadOrigin(r.Element(Ns + "origin")!)));
            var histories = root.Element(Ns + "requirement-histories")!.Elements(XName.Get("requirement-history", DiagramRequirementHistoryXml.Namespace))
                .Select(h => DiagramRequirementHistoryXml.Read(EngineeringXmlText.Render(h)));
            return new(Id(root, "document"), ReadSelection(root.Element(Ns + "selected-root")!), states, revisions, histories);
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw Invalid("Invalid recursive block graph XML: " + error.Message); }
    }

    private static XElement WriteOrigin(RequirementRevisionOrigin origin) => new(Ns + "origin",
        new XAttribute("actor-kind", origin.ActorKind), new XAttribute("actor", origin.Actor),
        new XAttribute("at", origin.RecordedAt.ToString("O", CultureInfo.InvariantCulture)), new XElement(Ns + "summary", origin.Summary),
        origin.Sources.Select(s => new XElement(Ns + "source", new XAttribute("document", s.DocumentId), new XAttribute("revision", s.Revision),
            s.Page is { } page ? new XAttribute("page", page) : null, s.Table is { } table ? new XAttribute("table", table) : null,
            s.PartVariant is { } variant ? new XAttribute("part-variant", variant) : null)),
        origin.InputIds.Select(id => new XElement(Ns + "input", Attr("ref", id))));

    private static RequirementRevisionOrigin ReadOrigin(XElement origin) => new(
        Enum.Parse<RequirementRevisionActor>(Text(origin, "actor-kind")), Text(origin, "actor"), XmlConvert.ToDateTimeOffset(Text(origin, "at")),
        origin.Element(Ns + "summary")!.Value,
        origin.Elements(Ns + "source").Select(s => new SourceReference(Text(s, "document"), Text(s, "revision"),
            s.Attribute("page") is { } page ? int.Parse(page.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null,
            (string?)s.Attribute("table"), (string?)s.Attribute("part-variant"))).ToImmutableArray(),
        origin.Elements(Ns + "input").Select(i => Id(i, "ref")).ToImmutableArray());

    private static XElement Selection(string name, BlockSelection selection) => new(Ns + name,
        Attr("block", selection.BlockId), Attr("state", selection.StateId), Attr("revision", selection.RevisionId));
    private static BlockSelection ReadSelection(XElement e) => new(Id(e, "block"), Id(e, "state"), Id(e, "revision"));
    private static string Text(XElement element, string name) => element.Attribute(name)!.Value;
    private static Guid Id(XElement element, string name) => Guid.ParseExact(Text(element, name), "D");
    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
    private static AutomationException Invalid(string message) => new("invalid_recursive_block_graph_xml", message);
}
