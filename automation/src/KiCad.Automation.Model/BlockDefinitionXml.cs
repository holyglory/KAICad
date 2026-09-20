using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

public static class BlockDefinitionXml
{
    public const string Namespace = "urn:kicad:automation:block-definition:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null }; AddSchema(set); set.Compile(); return set;
    });
    internal static void AddSchema(XmlSchemaSet set)
    {
        var assembly = typeof(BlockDefinitionXml).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("block-definition-v1.xsd", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(null, reader);
    }

    public static string Write(BlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition); definition.Validate();
        var root = new XElement(Ns + "definition", new XAttribute("version", 1));
        foreach (var facet in Enum.GetValues<BlockDefinitionFacet>())
        {
            var choice = facet switch { BlockDefinitionFacet.Purpose => definition.Purpose, BlockDefinitionFacet.Type => definition.Type,
                BlockDefinitionFacet.Manufacturer => definition.Manufacturer, BlockDefinitionFacet.Family => definition.Family,
                BlockDefinitionFacet.Model => definition.Model, BlockDefinitionFacet.OrderablePart => definition.OrderablePart,
                BlockDefinitionFacet.Package => definition.Package, _ => null };
            if (choice is not null) root.Add(WriteChoice(Name(facet), choice, value => new XElement(Ns + "value", value)));
        }
        if (definition.KnowledgeClass is { } knowledge)
            root.Add(WriteChoice("knowledge-class", knowledge, value => new XElement(Ns + "value",
                new XAttribute("library", value.LibraryId.ToString("D")), new XAttribute("revision", value.LibraryRevision),
                new XAttribute("class", value.ClassId.ToString("D")))));
        new XDocument(root).Validate(Schema.Value, null); return EngineeringXmlText.Render(root);
    }

    public static BlockDefinition Read(string xml)
    {
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "definition") throw BlockDefinition.Invalid("Use the supported block-definition XML root and namespace.");
            new XDocument(root).Validate(Schema.Value, null);
            var result = BlockDefinition.Empty;
            foreach (var facet in Enum.GetValues<BlockDefinitionFacet>())
                if (root.Element(Ns + Name(facet)) is { } element) result = result.With(facet, ReadChoice(element, value => value.Value));
            if (root.Element(Ns + "knowledge-class") is { } knowledge)
                result = result with { KnowledgeClass = ReadChoice(knowledge, value => new KnowledgeClassReference(
                    Guid.ParseExact(Text(value, "library"), "D"), Text(value, "revision"), Guid.ParseExact(Text(value, "class"), "D"))) };
            result.Validate(); return result;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw BlockDefinition.Invalid("Invalid definition XML: " + error.Message); }
    }

    private static XElement WriteChoice<T>(string name, DefinitionChoice<T> choice, Func<T, XElement> value) => new(Ns + name,
        new XAttribute("state", choice.State), new XAttribute("strength", choice.Strength), new XAttribute("verification", choice.Verification),
        new XElement(Ns + "applicability", choice.Applicability), choice.UnknownReason is { } reason ? new XElement(Ns + "unknown-reason", reason) : null,
        new XElement(Ns + "values", choice.Values.Select(value)), choice.Sources.Select(s => DiagramRevisionOriginXml.Source(Ns, s)));
    private static DefinitionChoice<T> ReadChoice<T>(XElement element, Func<XElement, T> value) => new(
        Enum.Parse<DefinitionChoiceState>(Text(element, "state")), element.Element(Ns + "values")!.Elements(Ns + "value").Select(value).ToImmutableArray(),
        Enum.Parse<GuidanceStrength>(Text(element, "strength")), element.Element(Ns + "applicability")!.Value,
        element.Elements(Ns + "source").Select(DiagramRevisionOriginXml.ReadSource).ToImmutableArray(),
        Enum.Parse<VerificationState>(Text(element, "verification")), element.Element(Ns + "unknown-reason")?.Value);
    internal static string Name(BlockDefinitionFacet facet) => facet switch
    {
        BlockDefinitionFacet.Purpose => "purpose", BlockDefinitionFacet.Type => "type", BlockDefinitionFacet.Manufacturer => "manufacturer",
        BlockDefinitionFacet.Family => "family", BlockDefinitionFacet.Model => "model", BlockDefinitionFacet.OrderablePart => "orderable-part",
        BlockDefinitionFacet.Package => "package", _ => throw BlockDefinition.Invalid("Unsupported definition facet.")
    };
    private static string Text(XElement e, string name) => e.Attribute(name)!.Value;
}
