using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>One typed design document, including engineering intent and native hierarchy data.
/// Unresolved identity bindings and explicit native coverage gaps survive storage; neither
/// successful serialization nor a valid schema authorizes applying the document to an editor.</summary>
public static class SchematicDesignXml
{
    public const string Namespace = "urn:kicad:automation:design:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var result = EngineeringDesignXml.CreateSchemaSet();
        using var native = XmlReader.Create(new StringReader(SchematicDataXml.ExportSchema()), Settings());
        result.Add(SchematicDataXml.Namespace, native);
        var assembly = typeof(SchematicDesignXml).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Schemas.design-v1.xsd", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = XmlReader.Create(stream, Settings());
        result.Add(Namespace, reader); result.Compile();
        return result;
    });

    public static SchematicDesign Read(string xml, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        try
        {
            xml = StripUtf8Bom(xml);
            var root = Parse(xml);
            if (root.Name != Ns + "design") throw Invalid("Expected the supported design XML namespace and root.");
            new XDocument(root).Validate(Schema.Value, null);
            var engineering = EngineeringDesignXml.Read(SchematicDataXml.Render(root.Element(
                XName.Get("engineering-design", EngineeringDesignXml.Namespace))!), libraries);
            var schematic = SchematicDataXml.Read(SchematicDataXml.Render(root.Element(
                XName.Get("schematic-data", SchematicDataXml.Namespace))!)) as SchematicHierarchyData
                ?? throw Invalid("A design requires a typed schematic hierarchy, not an isolated native object.");
            return new(engineering, schematic,
                root.Element(Ns + "sheet-bindings")!.Elements(Ns + "sheet").Select(s => new SchematicSheetBinding(Id(s, "model"),
                    s.Elements(Ns + "path-item").Select(p => Id(p, "id")).ToArray())).ToArray(),
                root.Element(Ns + "symbol-bindings")!.Elements(Ns + "symbol").Select(s =>
                    new SchematicSymbolBinding(Id(s, "model"), Id(s, "native"))).ToArray());
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        {
            throw Invalid("Invalid design XML: " + error.Message);
        }
    }

    private static string StripUtf8Bom(string xml) => xml.Length > 0 && xml[0] == '\uFEFF' ? xml[1..] : xml;

    public static string Write(SchematicDesign design, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        var root = new XElement(Ns + "design", new XAttribute("version", 1),
            Parse(EngineeringDesignXml.Write(design.Engineering, libraries)),
            Parse(SchematicDataXml.Write(design.Schematic)),
            new XElement(Ns + "sheet-bindings", design.SheetBindings.OrderBy(s => s.SheetInstanceId)
                .ThenBy(s => SchematicDesignBindings.PathKey(s.NativePath), StringComparer.Ordinal).Select(s =>
                    new XElement(Ns + "sheet", new XAttribute("model", s.SheetInstanceId),
                        s.NativePath.Select(id => new XElement(Ns + "path-item", new XAttribute("id", id)))))),
            new XElement(Ns + "symbol-bindings", design.SymbolBindings.OrderBy(s => s.SymbolOccurrenceId).ThenBy(s => s.NativeObjectId)
                .Select(s => new XElement(Ns + "symbol", new XAttribute("model", s.SymbolOccurrenceId), new XAttribute("native", s.NativeObjectId)))));
        try { new XDocument(root).Validate(Schema.Value, null); }
        catch (XmlSchemaException error) { throw Invalid(error.Message); }
        return SchematicDataXml.Render(root);
    }

    private static XElement Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), Settings());
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo).Root ?? throw Invalid("Missing design root.");
    }
    // SA-05: declared source paths are data, not implicit I/O or executable instructions.
    private static XmlReaderSettings Settings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
    private static Guid Id(XElement element, string name) => Guid.ParseExact(element.Attribute(name)!.Value, "D");
    private static AutomationException Invalid(string message) => new("invalid_design_xml", message);
}
