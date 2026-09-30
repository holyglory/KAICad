using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

public static class DiagramRefinementInputXml
{
    public const string Namespace = "urn:kicad:automation:refinement-input:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        Add(set, "requirement-history-v1.xsd"); AddSchema(set); set.Compile(); return set;
    });
    internal static void AddSchema(XmlSchemaSet set) => Add(set, "refinement-input-v1.xsd");
    private static void Add(XmlSchemaSet set, string file)
    {
        var assembly = typeof(DiagramRefinementInputXml).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(null, reader);
    }

    public static string Write(DiagramRefinementInput input)
    {
        ArgumentNullException.ThrowIfNull(input); input.Validate();
        var root = new XElement(Ns + "refinement-input", new XAttribute("version", 1), Attr("id", input.Id),
            Attr("document", input.DocumentId), new XAttribute("source-sha256", input.SourceSha256),
            DiagramRevisionOriginXml.Write(Ns, input.Origin), new XElement(Ns + "prompt", input.Prompt),
            new XElement(Ns + "block-path", input.BlockPath.Select(p => new XElement(Ns + "block", Attr("id", p.BlockId),
                Attr("state", p.StateId), Attr("revision", p.RevisionId)))),
            new XElement(Ns + "connection-path", input.ConnectionPath.Select(p => new XElement(Ns + "connection", Attr("id", p.ConnectionId),
                Attr("state", p.StateId), Attr("revision", p.RevisionId)))),
            new XElement(Ns + "attachments", input.Attachments.Select(a => new XElement(Ns + "attachment", Attr("id", a.Id),
                new XAttribute("asset", a.AssetPath), new XAttribute("sha256", a.ContentSha256), new XAttribute("bytes", a.ByteCount),
                new XAttribute("media-type", a.MediaType), new XElement(Ns + "original-name", a.OriginalName),
                a.Source is { } source ? DiagramRevisionOriginXml.Source(Ns, source) : null))));
        new XDocument(root).Validate(Schema.Value, null); return EngineeringXmlText.Render(root);
    }

    public static DiagramRefinementInput Read(string xml)
    {
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "refinement-input") throw DiagramRefinementInput.Invalid("Use the supported refinement-input XML root and namespace.");
            new XDocument(root).Validate(Schema.Value, null);
            var input = new DiagramRefinementInput(Id(root, "id"), Id(root, "document"), Text(root, "source-sha256"),
                [.. root.Element(Ns + "block-path")!.Elements(Ns + "block").Select(p => new BlockSelection(Id(p, "id"), Id(p, "state"), Id(p, "revision")))],
                [.. root.Element(Ns + "connection-path")!.Elements(Ns + "connection").Select(p => new ConnectionSelection(Id(p, "id"), Id(p, "state"), Id(p, "revision")))],
                root.Element(Ns + "prompt")!.Value, DiagramRevisionOriginXml.Read(root.Element(Ns + "origin")!),
                [.. root.Element(Ns + "attachments")!.Elements(Ns + "attachment").Select(a => new DiagramRefinementAttachment(
                    Id(a, "id"), a.Element(Ns + "original-name")!.Value, Text(a, "asset"), Text(a, "sha256"),
                    long.Parse(Text(a, "bytes"), NumberStyles.None, CultureInfo.InvariantCulture), Text(a, "media-type"),
                    a.Element(Ns + "source") is { } source ? DiagramRevisionOriginXml.ReadSource(source) : null))]);
            input.Validate(); return input;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw DiagramRefinementInput.Invalid("Invalid refinement input XML: " + error.Message); }
    }

    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
    private static string Text(XElement element, string attribute) => element.Attribute(attribute)!.Value;
    private static Guid Id(XElement element, string attribute) => Guid.ParseExact(Text(element, attribute), "D");
}
