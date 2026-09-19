using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Library-owned knowledge and design-owned instance binding fragments.
/// Neither document is a complete schematic. Unknown fields fail instead of being dropped.</summary>
public static class ComponentKnowledgeXml
{
    public const string Namespace = "urn:kicad:automation:knowledge:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var schema = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(ComponentKnowledgeXml).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("component-knowledge-v1.xsd", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using XmlReader reader = XmlReader.Create(stream, Settings());
        schema.Add(Namespace, reader);
        schema.Compile();
        return schema;
    });

    public static ComponentKnowledgeLibrary ReadLibrary(string xml)
    {
        XElement root = Read(xml, "component-library");
        var result = new ComponentKnowledgeLibrary(Id(root), Text(root, "revision"),
            root.Elements(Ns + "class").Select(c => new ComponentClass(Id(c), Text(c, "name"), OptionalId(c, "base"),
                c.Elements(Ns + "guidance").Select(ReadStatement).ToArray())).ToArray());
        ComponentGuidance.Validate(result);
        return result;
    }

    public static ComponentKnowledgeBinding ReadBinding(string xml, ComponentKnowledgeLibrary library)
    {
        XElement root = Read(xml, "component-binding");
        var result = new ComponentKnowledgeBinding(Id(root, "component"), Id(root, "library"),
            Text(root, "library-revision"), Id(root, "class"), root.Elements(Ns + "guidance").Select(ReadStatement).ToArray());
        ComponentGuidance.Resolve(library, result);
        return result;
    }

    public static string WriteLibrary(ComponentKnowledgeLibrary library)
    {
        ComponentGuidance.Validate(library);
        return EngineeringXmlText.Render(E("component-library", A("version", 1), A("id", library.Id), A("revision", library.Revision),
            library.Classes.OrderBy(c => c.Id).Select(c => E("class", A("id", c.Id), A("name", c.Name),
                c.BaseClassId is Guid parent ? A("base", parent) : null,
                c.Guidance.OrderBy(s => s.Id).Select(WriteStatement)))));
    }

    public static string WriteBinding(ComponentKnowledgeBinding binding, ComponentKnowledgeLibrary library)
    {
        ComponentGuidance.Resolve(library, binding);
        return EngineeringXmlText.Render(E("component-binding", A("version", 1), A("component", binding.ComponentInstanceId),
            A("library", binding.LibraryId), A("library-revision", binding.LibraryRevision), A("class", binding.ClassId),
            binding.Guidance.OrderBy(s => s.Id).Select(WriteStatement)));
    }

    private static XElement Read(string xml, string expected)
    {
        try
        {
            using var input = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(input, Settings());
            // Preserve whitespace in the statement text, including multi-line instructions.
            XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.Root?.Name != Ns + expected)
                throw new AutomationException("invalid_knowledge_xml", "Expected " + expected + " in the supported knowledge namespace.");
            document.Validate(Schema.Value, null);
            return document.Root;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException)
        {
            throw new AutomationException("invalid_knowledge_xml", error.Message);
        }
    }

    internal static GuidanceStatement ReadStatement(XElement s) => new(Id(s), Text(s, "key"), Text(s, "category"),
        s.Element(Ns + "text")!.Value, Enum.Parse<GuidanceStrength>(Text(s, "strength")), Text(s, "applicability"),
        s.Elements(Ns + "source").Select(p => new SourceReference(Text(p, "document"), Text(p, "revision"),
            p.Attribute("page") is XAttribute page ? ReadPage(page.Value) : null,
            (string?)p.Attribute("table"), (string?)p.Attribute("part-variant"))).ToArray(),
        Enum.Parse<VerificationState>(Text(s, "verification")), OptionalId(s, "replaces"), (string?)s.Element(Ns + "exception-rationale"),
        s.Element(Ns + "quantity") is XElement quantity ? ReadQuantity(quantity) : null);

    private static GuidanceQuantity ReadQuantity(XElement value)
    {
        decimal? Number(XElement element, string name)
        {
            if (element.Attribute(name) is not XAttribute attribute) return null;
            try
            {
                decimal number = XmlConvert.ToDecimal(attribute.Value);
                if (CanonicalDecimal(attribute.Value) != CanonicalDecimal(XmlConvert.ToString(number)))
                    throw new AutomationException("invalid_quantity", "Quantity precision exceeds the supported decimal representation; no rounded value was imported.");
                return number;
            }
            catch (Exception error) when (error is FormatException or OverflowException)
            { throw new AutomationException("invalid_quantity", "Quantity values must fit the supported decimal range."); }
        }
        static string CanonicalDecimal(string input)
        {
            string text = input.Trim(); bool negative = text.StartsWith('-');
            text = text.TrimStart('+', '-');
            int point = text.IndexOf('.');
            string integer = (point < 0 ? text : text[..point]).TrimStart('0');
            string fraction = point < 0 ? "" : text[(point + 1)..].TrimEnd('0');
            if (integer.Length == 0) integer = "0";
            return (negative && (integer != "0" || fraction.Length != 0) ? "-" : "")
                + integer + (fraction.Length == 0 ? "" : "." + fraction);
        }
        var tolerance = value.Element(Ns + "tolerance");
        return new(Enum.Parse<ParameterKind>(Text(value, "kind")), Text(value, "unit"),
            Number(value, "nominal"), Number(value, "minimum"), Number(value, "maximum"),
            tolerance is null ? null : new(Enum.Parse<ToleranceKind>(Text(tolerance, "kind")),
                Number(tolerance, "minus")!.Value, Number(tolerance, "plus")!.Value),
            (string?)value.Attribute("unknown-reason"));
    }

    private static int ReadPage(string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page))
            throw new AutomationException("invalid_knowledge_xml", "Source page must fit a positive integer.");
        return page;
    }

    internal static XElement WriteStatement(GuidanceStatement s) => E("guidance", A("id", s.Id), A("key", s.Key),
        A("category", s.Category), A("strength", s.Strength), A("applicability", s.Applicability), A("verification", s.Verification),
        s.Replaces is Guid target ? A("replaces", target) : null, E("text", s.Text),
        s.ExceptionRationale is string rationale ? E("exception-rationale", rationale) : null,
        s.Quantity is { } q ? E("quantity", A("kind", q.Kind), A("unit", q.Unit),
            q.Nominal is decimal nominal ? A("nominal", XmlConvert.ToString(nominal)) : null,
            q.Minimum is decimal minimum ? A("minimum", XmlConvert.ToString(minimum)) : null,
            q.Maximum is decimal maximum ? A("maximum", XmlConvert.ToString(maximum)) : null,
            q.UnknownReason is string unknown ? A("unknown-reason", unknown) : null,
            q.Tolerance is { } tolerance ? E("tolerance", A("kind", tolerance.Kind),
                A("minus", XmlConvert.ToString(tolerance.Minus)), A("plus", XmlConvert.ToString(tolerance.Plus))) : null) : null,
        s.Sources.Select(p => E("source", A("document", p.DocumentId), A("revision", p.Revision),
            p.Page is int page ? A("page", page) : null, p.Table is string table ? A("table", table) : null,
            p.PartVariant is string variant ? A("part-variant", variant) : null)));

    // SA-05: no DTD expansion or implicit retrieval of documents named in the XML.
    private static XmlReaderSettings Settings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
    private static XAttribute A(string name, object value) => new(name, value);
    private static string Text(XElement e, string attribute) => e.Attribute(attribute)!.Value;
    private static Guid Id(XElement e, string attribute = "id") => Guid.TryParseExact(Text(e, attribute), "D", out Guid id)
        ? id : throw new AutomationException("invalid_knowledge_xml", "Expected an exact UUID for " + attribute + ".");
    private static Guid? OptionalId(XElement e, string attribute) => e.Attribute(attribute) is null ? null : Id(e, attribute);
}
