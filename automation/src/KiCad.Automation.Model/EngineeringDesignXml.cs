using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Strict composition of electrical, structural and instance-guidance data.
/// No file I/O, library upgrades, schematic generation or native mutation occurs here.</summary>
public static class EngineeringDesignXml
{
    public const string Namespace = "urn:kicad:automation:engineering-design:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly XNamespace KnowledgeNs = ComponentKnowledgeXml.Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(CreateSchemaSet);

    /// <summary>Fresh schema set for composing a typed native design envelope. Callers do not
    /// receive the shared validator and cannot change another reader's schema.</summary>
    public static XmlSchemaSet CreateSchemaSet()
    {
        var result = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(EngineeringDesignXml).Assembly;
        foreach (string name in new[] { "circuit-v1.xsd", "structure-v1.xsd", "component-knowledge-v1.xsd", "engineering-design-v1.xsd" })
        {
            string resource = assembly.GetManifestResourceNames().Single(r => r.EndsWith(name, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            result.Add(null, reader);
        }
        result.Compile();
        return result;
    }

    public static EngineeringDesign Read(string xml, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        try
        {
            XElement root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "engineering-design") throw EngineeringDesign.Invalid("Expected supported engineering-design XML.");
            new XDocument(root).Validate(Schema.Value, null);
            string Section(XElement element) => EngineeringXmlText.Render(element);
            Circuit circuit = CircuitXml.Read(Section(root.Element(XName.Get("circuit", CircuitXml.Namespace))!));
            StructuralDiagram structure = StructuralDiagramXml.Read(Section(root.Element(XName.Get("structure", StructuralDiagramXml.Namespace))!), circuit);
            var references = root.Element(Ns + "knowledge-libraries")!.Elements(Ns + "library")
                .Select(e => new HardwareLibrary(Guid.ParseExact(e.Attribute("id")!.Value, "D"),
                    e.Attribute("path")!.Value, e.Attribute("revision")!.Value)).ToArray();
            var available = EngineeringDesign.IndexLibraries(libraries);
            var bindings = new List<ComponentKnowledgeBinding>();
            foreach (var element in root.Element(Ns + "component-bindings")!.Elements(KnowledgeNs + "component-binding"))
            {
                Guid id = Guid.ParseExact(element.Attribute("library")!.Value, "D");
                if (!available.TryGetValue(id, out var library))
                    throw new AutomationException("missing_knowledge_library", "Supply the referenced knowledge library: " + id);
                bindings.Add(ComponentKnowledgeXml.ReadBinding(Section(element), library));
            }
            var unresolved = root.Element(Ns + "unresolved-guidance-bindings")?.Elements(Ns + "binding")
                .Select(e => new UnresolvedGuidanceBinding(Guid.ParseExact(e.Attribute("component")!.Value, "D"),
                    Enum.Parse<ComponentReferenceChangeKind>(e.Attribute("change")!.Value), e.Element(Ns + "reason")!.Value,
                    e.Elements(Ns + "candidate").Select(c => Guid.ParseExact(c.Attribute("component")!.Value, "D")).ToArray())).ToArray();
            var result = new EngineeringDesign(circuit, structure, references, bindings, unresolved);
            result.Validate(libraries);
            return result;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        {
            throw EngineeringDesign.Invalid("Invalid engineering XML: " + error.Message);
        }
    }

    public static string Write(EngineeringDesign design, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        design.Validate(libraries);
        var available = EngineeringDesign.IndexLibraries(libraries);
        var root = new XElement(Ns + "engineering-design", new XAttribute("version", 1),
            EngineeringXmlText.Parse(CircuitXml.Write(design.Circuit)),
            EngineeringXmlText.Parse(StructuralDiagramXml.Write(design.Structure, design.Circuit)),
            new XElement(Ns + "knowledge-libraries", design.KnowledgeLibraries.OrderBy(l => l.Id).Select(l =>
                new XElement(Ns + "library", new XAttribute("id", l.Id), new XAttribute("path", l.Path), new XAttribute("revision", l.Revision)))),
            new XElement(Ns + "component-bindings", design.ComponentBindings.OrderBy(b => b.ComponentInstanceId).Select(b =>
                EngineeringXmlText.Parse(ComponentKnowledgeXml.WriteBinding(b, available[b.LibraryId])))),
            design.UnresolvedGuidanceBindings is not { Count: > 0 } ? null : new XElement(Ns + "unresolved-guidance-bindings",
                design.UnresolvedGuidanceBindings.OrderBy(b => b.ComponentInstanceId).Select(b => new XElement(Ns + "binding",
                    new XAttribute("component", b.ComponentInstanceId), new XAttribute("change", b.Change), new XElement(Ns + "reason", b.Reason),
                    b.CandidateComponentIds.Order().Select(id => new XElement(Ns + "candidate", new XAttribute("component", id)))))));
        new XDocument(root).Validate(Schema.Value, null);
        return EngineeringXmlText.Render(root);
    }
}
