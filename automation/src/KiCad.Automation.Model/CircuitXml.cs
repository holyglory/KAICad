using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Strict codec for the electrical section, not a native design importer.</summary>
public static class CircuitXml
{
    public const string Namespace = "urn:kicad:automation:circuit:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var result = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(CircuitXml).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("circuit-v1.xsd", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using XmlReader reader = XmlReader.Create(stream, ReaderSettings());
        result.Add(Namespace, reader);
        result.Compile();
        return result;
    });

    public static Circuit Read(string xml)
    {
        try
        {
            using var input = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(input, ReaderSettings());
            XDocument document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (document.Root?.Name != Ns + "circuit")
                throw Circuit.Invalid("Expected a supported circuit namespace and root element.");
            document.Validate(Schema.Value, null);
            XElement root = document.Root;
            XElement[] Rows(string group, string element) => root.Element(Ns + group)!.Elements(Ns + element).ToArray();
            var circuit = new Circuit(Id(root),
                Rows("parts", "part").Select(p => new PartDefinition(Id(p), Text(p, "name"), Int(p, "units"),
                    p.Elements(Ns + "pin").Select(pin => new PartPin(Text(pin, "number"), Text(pin, "name"), Int(pin, "unit"))).ToArray())).ToArray(),
                Rows("sheets", "sheet").Select(s => new SheetDefinition(Id(s), Text(s, "name"),
                    s.Elements(Ns + "component").Select(c => new ComponentDefinition(Id(c), Id(c, "part"), Text(c, "value"))).ToArray())).ToArray(),
                Rows("sheet-instances", "instance").Select(s => new SheetInstance(Id(s), Id(s, "definition"),
                    s.Attribute("parent") is null ? null : Id(s, "parent"))).ToArray(),
                Rows("components", "instance").Select(c => new ComponentInstance(Id(c), Id(c, "definition"), Id(c, "sheet"), Text(c, "reference"))).ToArray(),
                Rows("nets", "net").Select(n => new CircuitNet(Id(n), Text(n, "name"),
                    n.Elements(Ns + "pin").Select(p => new PinEndpoint(Id(p, "component"), Text(p, "number"))).ToArray())).ToArray(),
                Rows("symbols", "symbol").Select(s => new SymbolOccurrence(Id(s), Id(s, "component"), Int(s, "unit"),
                    s.Element(Ns + "placement") is XElement p
                        ? new SymbolPlacement(Decimal(p, "x-mm"), Decimal(p, "y-mm"), Int(p, "rotation-deg"),
                            Bool(p, "mirror-x"), Bool(p, "mirror-y"), Bool(p, "locked")) : null,
                    s.Attribute("sheet-instance") is null ? null : Id(s, "sheet-instance"))).ToArray());
            circuit.Validate();
            return circuit;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        {
            throw Circuit.Invalid("Invalid electrical XML: " + error.Message);
        }
    }

    public static string Write(Circuit circuit)
    {
        circuit.Validate();
        // Stable ordering avoids file churn after equivalent enumeration changes.
        var root = E("circuit", A("id", circuit.Id), A("version", 1),
            E("parts", circuit.Parts.OrderBy(p => p.Id).Select(p => E("part", A("id", p.Id), A("name", p.Name), A("units", p.Units),
                p.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal).Select(pin => E("pin", A("number", pin.Number), A("name", pin.Name), A("unit", pin.Unit)))))),
            E("sheets", circuit.Sheets.OrderBy(s => s.Id).Select(s => E("sheet", A("id", s.Id), A("name", s.Name),
                s.Components.OrderBy(c => c.Id).Select(c => E("component", A("id", c.Id), A("part", c.PartId), A("value", c.Value)))))),
            E("sheet-instances", circuit.SheetInstances.OrderBy(s => s.Id).Select(s => E("instance", A("id", s.Id), A("definition", s.DefinitionId),
                s.ParentId is Guid parent ? A("parent", parent) : null))),
            E("components", circuit.Components.OrderBy(c => c.Id).Select(c => E("instance", A("id", c.Id), A("definition", c.DefinitionId), A("sheet", c.SheetInstanceId), A("reference", c.Reference)))),
            E("nets", circuit.Nets.OrderBy(n => n.Id).Select(n => E("net", A("id", n.Id), A("name", n.Name),
                n.Pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).Select(p => E("pin", A("component", p.ComponentId), A("number", p.Pin)))))),
            E("symbols", circuit.Symbols.OrderBy(s => s.Id).Select(s => E("symbol", A("id", s.Id), A("component", s.ComponentId), A("unit", s.Unit),
                s.SheetInstanceId is Guid sheet ? A("sheet-instance", sheet) : null,
                s.Placement is SymbolPlacement p ? E("placement", A("x-mm", p.XMillimeters), A("y-mm", p.YMillimeters),
                    A("rotation-deg", p.RotationDegrees), A("mirror-x", p.MirrorX), A("mirror-y", p.MirrorY), A("locked", p.Locked)) : null))));
        return EngineeringXmlText.Render(root);
    }

    // SA-05: source documents are data, never instructions or external resources.
    private static XmlReaderSettings ReaderSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
    private static XAttribute A(string name, object value) => new(name, value is decimal d ? d.ToString("G29", CultureInfo.InvariantCulture) : value);
    private static string Text(XElement e, string name) => e.Attribute(name)!.Value;
    private static Guid Id(XElement e, string name = "id") => Guid.ParseExact(Text(e, name), "D");
    private static int Int(XElement e, string name) => XmlConvert.ToInt32(Text(e, name));
    private static decimal Decimal(XElement e, string name) => XmlConvert.ToDecimal(Text(e, name));
    private static bool Bool(XElement e, string name) => XmlConvert.ToBoolean(Text(e, name));
}
