using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Typed, lossless requirement history. This document stores field
/// revisions and provenance, not an opaque schematic or inferred past edits.</summary>
public static class DiagramRequirementHistoryXml
{
    public const string Namespace = "urn:kicad:automation:requirement-history:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(DiagramRequirementHistoryXml).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("requirement-history-v1.xsd", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(null, reader); set.Compile(); return set;
    });

    public static string Write(DiagramRequirementHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var root = new XElement(Ns + "requirement-history", new XAttribute("version", 1),
            Attr("document", history.Scope.DocumentId), Attr("owner", history.Scope.OwnerId), Attr("design-state", history.Scope.DesignStateId),
            history.Revisions.Select(r => new XElement(Ns + "revision", Attr("id", r.Id),
                r.ParentId is Guid parent ? Attr("parent", parent) : null,
                new XElement(Ns + "origin", new XAttribute("actor-kind", r.Origin.ActorKind), new XAttribute("actor", r.Origin.Actor),
                    new XAttribute("at", r.Origin.RecordedAt.ToString("O", CultureInfo.InvariantCulture)),
                    new XElement(Ns + "summary", r.Origin.Summary),
                    r.Origin.Sources.Select(s => new XElement(Ns + "source", new XAttribute("document", s.DocumentId),
                        new XAttribute("revision", s.Revision), s.Page is int page ? new XAttribute("page", page) : null,
                        s.Table is { } table ? new XAttribute("table", table) : null,
                        s.PartVariant is { } variant ? new XAttribute("part-variant", variant) : null)),
                    r.Origin.InputIds.Select(id => new XElement(Ns + "input", Attr("ref", id)))),
                new XElement(Ns + "requirements", new XElement(Ns + "general", r.Requirements.General),
                    new XElement(Ns + "schematic", r.Requirements.Schematic), new XElement(Ns + "routing", r.Requirements.Routing)),
                r.Restorations.Select(s => new XElement(Ns + "restored-field", new XAttribute("field", s.Field), Attr("source-revision", s.SourceRevisionId)))));
        return EngineeringXmlText.Render(root);
    }

    public static DiagramRequirementHistory Read(string xml)
    {
        if (xml is null) throw Invalid("A requirement history document is required.");
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "requirement-history") throw Invalid("Use the supported requirement history root and namespace.");
            new XDocument(root).Validate(Schema.Value, null);
            var scope = new DiagramRequirementScope(Id(root, "document"), Id(root, "owner"), Id(root, "design-state"));
            var revisions = root.Elements(Ns + "revision").Select(r =>
            {
                var origin = r.Element(Ns + "origin")!;
                var fields = r.Element(Ns + "requirements")!;
                return new DiagramRequirementRevision(Id(r, "id"), r.Attribute("parent") is null ? null : Id(r, "parent"),
                    new(fields.Element(Ns + "general")!.Value, fields.Element(Ns + "schematic")!.Value, fields.Element(Ns + "routing")!.Value),
                    new(Enum.Parse<RequirementRevisionActor>(Text(origin, "actor-kind")), Text(origin, "actor"),
                        XmlConvert.ToDateTimeOffset(Text(origin, "at")),
                        origin.Element(Ns + "summary")!.Value,
                        origin.Elements(Ns + "source").Select(s => new SourceReference(Text(s, "document"), Text(s, "revision"),
                            s.Attribute("page") is { } p ? int.Parse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null,
                            (string?)s.Attribute("table"), (string?)s.Attribute("part-variant"))).ToImmutableArray(),
                        origin.Elements(Ns + "input").Select(s => Id(s, "ref")).ToImmutableArray()),
                    r.Elements(Ns + "restored-field").Select(s => new RequirementFieldRestoration(
                        Enum.Parse<DiagramRequirementField>(Text(s, "field")), Id(s, "source-revision"))).ToImmutableArray());
            }).ToImmutableArray();
            return new(scope, revisions);
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw Invalid("Invalid requirement history XML: " + error.Message); }
    }

    private static string Text(XElement element, string name) => element.Attribute(name)!.Value;
    private static Guid Id(XElement element, string name) => Guid.ParseExact(Text(element, name), "D");
    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
    private static AutomationException Invalid(string message) => new("invalid_requirement_history_xml", message);
}
