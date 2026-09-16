using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Architectural intent and realization section of engineering XML.
/// Not a schematic snapshot, native importer or inference of electrical connections.</summary>
public static class StructuralDiagramXml
{
    public const string Namespace = "urn:kicad:automation:structure:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var schema = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(StructuralDiagramXml).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("structure-v1.xsd", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using XmlReader reader = XmlReader.Create(stream, Settings());
        schema.Add(Namespace, reader);
        schema.Compile();
        return schema;
    });

    public static StructuralDiagram Read(string xml, Circuit circuit)
    {
        try
        {
            using var input = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(input, Settings());
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.Root?.Name != Ns + "structure") throw Invalid("Expected a supported structure namespace and root.");
            document.Validate(Schema.Value, null);
            XElement root = document.Root;
            IEnumerable<XElement> Rows(string group, string row) => root.Element(Ns + group)!.Elements(Ns + row);
            var result = new StructuralDiagram(Id(root),
                Rows("blocks", "block").Select(b => new StructuralBlock(Id(b), Text(b, "name"), OptionalId(b, "parent"),
                    b.Elements(Ns + "component").Select(c => Id(c, "ref")).ToArray())).ToArray(),
                Rows("ports", "port").Select(p => new StructuralPort(Id(p), Id(p, "block"), Text(p, "name"))).ToArray(),
                Rows("connections", "connection").Select(c => new StructuralConnection(Id(c), Id(c, "first-port"), Id(c, "second-port"),
                    Enum.Parse<StructuralConnectionKind>(Text(c, "kind")), c.Element(Ns + "description")!.Value,
                    c.Elements(Ns + "net").Select(n => Id(n, "ref")).ToArray())).ToArray(),
                Rows("statements", "statement").Select(s => new EngineeringStatement(Id(s), Id(s, "target"),
                    Enum.Parse<EngineeringStatementRole>(Text(s, "role")),
                    s.Attribute("strength") is null ? null : Enum.Parse<GuidanceStrength>(Text(s, "strength")),
                    s.Element(Ns + "text")!.Value,
                    s.Element(Ns + "pin-connection") is XElement pins
                        ? new PinConnectionDetail(Endpoint(pins.Element(Ns + "first")!), Endpoint(pins.Element(Ns + "second")!)) : null,
                    s.Elements(Ns + "derived-from").Select(p => Id(p, "ref")).ToArray(),
                    s.Elements(Ns + "source").Select(p => new SourceReference(Text(p, "document"), Text(p, "revision"),
                        p.Attribute("page") is XAttribute page ? int.Parse(page.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null,
                        (string?)p.Attribute("table"), (string?)p.Attribute("part-variant"))).ToArray())).ToArray(),
                root.Element(Ns + "unresolved-net-bindings")?.Elements(Ns + "binding").Select(b => new UnresolvedNetBinding(
                    Id(b, "owner"), Id(b, "former-net"), Enum.Parse<NetBindingChangeKind>(Text(b, "change")),
                    b.Element(Ns + "reason")!.Value, b.Elements(Ns + "candidate").Select(n => Id(n, "ref")).ToArray())).ToArray(),
                root.Element(Ns + "unresolved-component-references")?.Elements(Ns + "reference").Select(r => new UnresolvedComponentReference(
                    Id(r, "owner"), Enum.Parse<ComponentReferenceSlot>(Text(r, "slot")), ComponentTarget(r.Element(Ns + "former")!),
                    Enum.Parse<ComponentReferenceChangeKind>(Text(r, "change")), r.Element(Ns + "reason")!.Value,
                    r.Elements(Ns + "candidate").Select(e => ComponentTarget(e)).ToArray())).ToArray());
            result.Validate(circuit);
            return result;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        {
            throw Invalid("Invalid structural XML: " + error.Message);
        }
    }

    public static string Write(StructuralDiagram diagram, Circuit circuit)
    {
        diagram.Validate(circuit);
        XElement root = E("structure", A("version", 1), A("id", diagram.Id),
            E("blocks", diagram.Blocks.OrderBy(b => b.Id).Select(b => E("block", A("id", b.Id), A("name", b.Name),
                b.ParentId is Guid parent ? A("parent", parent) : null,
                b.ComponentIds.Order().Select(id => E("component", A("ref", id)))))),
            E("ports", diagram.Ports.OrderBy(p => p.Id).Select(p => E("port", A("id", p.Id), A("block", p.BlockId), A("name", p.Name)))),
            E("connections", diagram.Connections.OrderBy(c => c.Id).Select(c => E("connection", A("id", c.Id),
                A("first-port", c.FirstPortId), A("second-port", c.SecondPortId), A("kind", c.Kind), E("description", c.Description),
                c.NetIds.Order().Select(id => E("net", A("ref", id)))))),
            E("statements", diagram.Statements.OrderBy(s => s.Id).Select(s => E("statement", A("id", s.Id), A("target", s.TargetId),
                A("role", s.Role), s.Strength is GuidanceStrength strength ? A("strength", strength) : null, E("text", s.Text),
                s.Connection is PinConnectionDetail pins ? E("pin-connection", Pin("first", pins.First), Pin("second", pins.Second)) : null,
                s.DerivedFrom.Order().Select(id => E("derived-from", A("ref", id))),
                s.Sources.Select(p => E("source", A("document", p.DocumentId), A("revision", p.Revision),
                    p.Page is int page ? A("page", page) : null, p.Table is string table ? A("table", table) : null,
                    p.PartVariant is string variant ? A("part-variant", variant) : null))))),
            diagram.UnresolvedNetBindings is not { Count: > 0 } ? null : E("unresolved-net-bindings",
                diagram.UnresolvedNetBindings.OrderBy(b => b.OwnerId).ThenBy(b => b.FormerNetId).Select(b => E("binding",
                    A("owner", b.OwnerId), A("former-net", b.FormerNetId), A("change", b.Change), E("reason", b.Reason),
                    b.CandidateNetIds.Order().Select(id => E("candidate", A("ref", id)))))),
            diagram.UnresolvedComponentReferences is not { Count: > 0 } ? null : E("unresolved-component-references",
                diagram.UnresolvedComponentReferences.OrderBy(r => r.OwnerId).ThenBy(r => r.Slot)
                    .ThenBy(r => r.FormerTarget.ComponentId).ThenBy(r => r.FormerTarget.PinNumber, StringComparer.Ordinal)
                    .Select(r => E("reference", A("owner", r.OwnerId), A("slot", r.Slot), A("change", r.Change),
                        ComponentTarget("former", r.FormerTarget), E("reason", r.Reason),
                        r.CandidateTargets.OrderBy(t => t.ComponentId).ThenBy(t => t.PinNumber, StringComparer.Ordinal)
                            .Select(t => ComponentTarget("candidate", t))))));
        // Entitize CR and attribute whitespace rather than letting XML newline normalization
        // silently change user-authored text or source coordinates on reload.
        return EngineeringXmlText.Render(root);
    }

    private static ComponentReferenceTarget ComponentTarget(XElement element) => new(Id(element, "component"), (string?)element.Attribute("pin"));
    private static XElement ComponentTarget(string name, ComponentReferenceTarget target) => E(name, A("component", target.ComponentId),
        target.PinNumber is null ? null : A("pin", target.PinNumber));
    private static XElement Pin(string name, PinEndpoint p) => E(name, A("component", p.ComponentId), A("number", p.Pin));
    private static PinEndpoint Endpoint(XElement e) => new(Id(e, "component"), Text(e, "number"));
    // SA-05: repository evidence stays data; no DTD expansion or implicit document retrieval.
    private static XmlReaderSettings Settings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
    private static XAttribute A(string name, object value) => new(name, value);
    private static string Text(XElement e, string attribute) => e.Attribute(attribute)!.Value;
    private static Guid Id(XElement e, string attribute = "id") => Guid.TryParseExact(Text(e, attribute), "D", out Guid id)
        ? id : throw Invalid("Expected an exact UUID for " + attribute + ".");
    private static Guid? OptionalId(XElement e, string attribute) => e.Attribute(attribute) is null ? null : Id(e, attribute);
    private static AutomationException Invalid(string message) => new("invalid_structural_xml", message);
}
