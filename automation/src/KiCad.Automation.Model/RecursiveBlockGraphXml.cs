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
        foreach (string name in new[] { "requirement-history-v1.xsd", "connection-archive-v1.xsd", "recursive-block-graph-v1.xsd" })
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
                Attr("id", s.Id), Attr("block", s.BlockId), new XAttribute("name", s.Name), Attr("head", s.HeadRevisionId),
                s.Archived ? new XAttribute("archived", true) : null,
                s.ForkedFrom is { } source ? Selection("forked-from", source) : null))),
            new XElement(Ns + "revisions", graph.Revisions.OrderBy(r => r.Selection.RevisionId).Select(r =>
                new XElement(Ns + "revision", Attr("id", r.Selection.RevisionId), Attr("block", r.Selection.BlockId), Attr("state", r.Selection.StateId),
                    r.ParentRevisionId is { } parent ? Attr("parent", parent) : null, new XAttribute("name", r.Name),
                    Attr("requirements", r.RequirementRevisionId), WriteOrigin(r.Origin),
                    new XElement(Ns + "children", r.Children.Select(c => Selection("child", c))),
                    r.RestoredFrom is { } source ? Selection("restored-from", source) : null,
                    r.Diagram is { } diagram ? WriteLocal(diagram) : null))),
            new XElement(Ns + "requirement-histories", graph.RequirementHistories.OrderBy(h => h.Scope.DesignStateId)
                .Select(h => EngineeringXmlText.Parse(DiagramRequirementHistoryXml.Write(h)))),
            graph.ConnectionArchives.IsEmpty ? null : new XElement(Ns + "connection-archives", graph.ConnectionArchives.OrderBy(a => a.OwnerBlockId)
                .Select(a => EngineeringXmlText.Parse(DiagramConnectionArchiveXml.Write(a)))),
            graph.ImplementationChanges.IsEmpty ? null : new XElement(Ns + "implementation-changes", graph.ImplementationChanges.Select(c =>
                new XElement(Ns + "change", Attr("id", c.Id), Attr("state", c.StateId), new XAttribute("kind", c.Kind),
                    new XAttribute("before-name", c.BeforeName), new XAttribute("after-name", c.AfterName),
                    new XAttribute("before-archived", c.BeforeArchived), new XAttribute("after-archived", c.AfterArchived), DiagramRevisionOriginXml.Write(Ns, c.Origin)))));
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
                new BlockDesignState(Id(s, "id"), Id(s, "block"), Text(s, "name"), Id(s, "head"),
                    s.Element(Ns + "forked-from") is { } source ? ReadSelection(source) : null, (bool?)s.Attribute("archived") ?? false));
            var revisions = root.Element(Ns + "revisions")!.Elements(Ns + "revision").Select(r =>
                new RecursiveBlockRevision(new(Id(r, "block"), Id(r, "state"), Id(r, "id")),
                    r.Attribute("parent") is null ? null : Id(r, "parent"), Text(r, "name"), Id(r, "requirements"),
                    r.Element(Ns + "children")!.Elements(Ns + "child").Select(ReadSelection).ToImmutableArray(),
                    ReadOrigin(r.Element(Ns + "origin")!), r.Element(Ns + "restored-from") is { } source ? ReadSelection(source) : null,
                    r.Element(Ns + "local-diagram") is { } diagram ? ReadLocal(diagram) : null));
            var histories = root.Element(Ns + "requirement-histories")!.Elements(XName.Get("requirement-history", DiagramRequirementHistoryXml.Namespace))
                .Select(h => DiagramRequirementHistoryXml.Read(EngineeringXmlText.Render(h)));
            var archives = root.Element(Ns + "connection-archives")?.Elements(XName.Get("connection-archive", DiagramConnectionArchiveXml.Namespace))
                .Select(a => DiagramConnectionArchiveXml.Read(EngineeringXmlText.Render(a))) ?? [];
            var changes = root.Element(Ns + "implementation-changes")?.Elements(Ns + "change").Select(c => new ImplementationChange(
                Id(c, "id"), Id(c, "state"), Enum.Parse<ImplementationChangeKind>(Text(c, "kind")), Text(c, "before-name"), Text(c, "after-name"),
                (bool)c.Attribute("before-archived")!, (bool)c.Attribute("after-archived")!, DiagramRevisionOriginXml.Read(c.Element(Ns + "origin")!))) ?? [];
            return new(Id(root, "document"), ReadSelection(root.Element(Ns + "selected-root")!), states, revisions, histories, archives, changes);
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
    private static XElement WriteLocal(BlockLocalDiagram diagram) => new(Ns + "local-diagram",
        new XElement(Ns + "interfaces", diagram.Interfaces.Select(i => new XElement(Ns + "interface", Attr("id", i.Id),
            new XAttribute("name", i.Name), new XElement(Ns + "intent", i.Intent)))),
        new XElement(Ns + "connections", diagram.Connections.Select(c => new XElement(Ns + "connection",
            Attr("connection", c.ConnectionId), Attr("state", c.StateId), Attr("revision", c.RevisionId)))),
        diagram.Notes.IsEmpty ? null : new XElement(Ns + "annotations", diagram.Notes.Select(WriteNote)));
    private static BlockLocalDiagram ReadLocal(XElement diagram) => new(
        diagram.Element(Ns + "interfaces")!.Elements(Ns + "interface").Select(i => new DiagramBoundaryInterface(
            Id(i, "id"), Text(i, "name"), i.Element(Ns + "intent")!.Value)).ToImmutableArray(),
        diagram.Element(Ns + "connections")!.Elements(Ns + "connection").Select(c => new ConnectionSelection(
            Id(c, "connection"), Id(c, "state"), Id(c, "revision"))).ToImmutableArray(),
        diagram.Element(Ns + "annotations")?.Elements(Ns + "annotation").Select(ReadNote).ToImmutableArray() ?? []);
    private static XElement WriteNote(DiagramAnnotation note) => new(Ns + "annotation", Attr("id", note.Id),
        new XAttribute("role", note.Role), new XAttribute("units", "diagram-unit"), DiagramRevisionOriginXml.Write(Ns, note.Origin),
        new XElement(Ns + "text", note.Text), new XElement(Ns + "target", new XAttribute("kind", note.Target.Kind),
            note.Target.TargetId is { } id ? Attr("ref", id) : null,
            note.Target.UnresolvedReason is { } reason ? new XElement(Ns + "unresolved-reason", reason) : null),
        note.Position is { } position ? WritePoint("position", position) : null,
        new XElement(Ns + "strokes", note.Strokes.Select(s => new XElement(Ns + "stroke", s.Points.Select(p => WritePoint("point", p))))));
    private static XElement WritePoint(string name, DiagramAnnotationPoint point) => new(Ns + name,
        new XAttribute("x", point.X), new XAttribute("y", point.Y));
    private static DiagramAnnotation ReadNote(XElement note)
    {
        var target = note.Element(Ns + "target")!;
        return new(Id(note, "id"), Enum.Parse<DiagramAnnotationRole>(Text(note, "role")), note.Element(Ns + "text")!.Value,
            new(Enum.Parse<DiagramAnnotationTargetKind>(Text(target, "kind")), target.Attribute("ref") is null ? null : Id(target, "ref"),
                target.Element(Ns + "unresolved-reason")?.Value), note.Element(Ns + "position") is { } position ? ReadPoint(position) : null,
            note.Element(Ns + "strokes")!.Elements(Ns + "stroke").Select(s => new DiagramAnnotationStroke(
                s.Elements(Ns + "point").Select(ReadPoint).ToImmutableArray())).ToImmutableArray(), DiagramRevisionOriginXml.Read(note.Element(Ns + "origin")!));
    }
    private static DiagramAnnotationPoint ReadPoint(XElement point) => DiagramAnnotationPoint.Parse(Text(point, "x"), Text(point, "y"));
    private static string Text(XElement element, string name) => element.Attribute(name)!.Value;
    private static Guid Id(XElement element, string name) => Guid.ParseExact(Text(element, name), "D");
    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
    private static AutomationException Invalid(string message) => new("invalid_recursive_block_graph_xml", message);
}
