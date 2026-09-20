using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

public sealed record PackagePad(string Number, string Name, bool Electrical);
public sealed record PinPadMapping(string PinNumber, string PadNumber, string PinName, string PadName);

/// <summary>Exact package realization for one electrical part/component. Pad and
/// pin strings are descriptive evidence; identity is owned by the declared part,
/// component and package revision. No positional or name-only matching is allowed.</summary>
public sealed record ComponentPackageMapping(Guid PartId, Guid ComponentId, string PackageId, string PackageRevision,
    ImmutableArray<PackagePad> Pads, ImmutableArray<PinPadMapping> PinMappings,
    ImmutableArray<string> ExplicitlyUnusedPads = default)
{
    public void Validate(Circuit circuit)
    {
        circuit.Validate();
        if (PartId == Guid.Empty || ComponentId == Guid.Empty || string.IsNullOrWhiteSpace(PackageId)
            || string.IsNullOrWhiteSpace(PackageRevision) || Pads.IsDefault || PinMappings.IsDefault
            || ExplicitlyUnusedPads.IsDefault)
            throw Invalid("Package mappings need exact part/component identities, a pinned package revision and explicit collections.");
        Text(PackageId); Text(PackageRevision);
        var part = circuit.Parts.SingleOrDefault(p => p.Id == PartId) ?? throw Invalid("The mapped part does not exist.");
        var component = circuit.Components.SingleOrDefault(c => c.Id == ComponentId) ?? throw Invalid("The mapped component does not exist.");
        var definition = circuit.Sheets.SelectMany(s => s.Components).SingleOrDefault(c => c.Id == component.DefinitionId)
            ?? throw Invalid("The mapped component definition does not exist.");
        if (definition.PartId != PartId) throw Invalid("The component does not instantiate the mapped part.");
        if (Pads.Any(p => p is null || string.IsNullOrWhiteSpace(p.Number) || string.IsNullOrWhiteSpace(p.Name))
            || Pads.Select(p => p.Number).Distinct(StringComparer.Ordinal).Count() != Pads.Length)
            throw Invalid("Package pad numbers must be explicit, nonempty and unique.");
        foreach (var pad in Pads) { Text(pad.Number); Text(pad.Name); }
        var padNumbers = Pads.Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
        var unused = ExplicitlyUnusedPads.ToHashSet(StringComparer.Ordinal);
        if (unused.Count != ExplicitlyUnusedPads.Length || unused.Any(p => !padNumbers.Contains(p)))
            throw Invalid("Explicitly unused pads must be distinct declared pad numbers.");
        var electricalPins = part.Pins.Select(p => (p.Number, p.Name)).ToHashSet();
        if (PinMappings.Length != electricalPins.Count || PinMappings.Select(p => p.PinNumber).Distinct(StringComparer.Ordinal).Count() != PinMappings.Length)
            throw Invalid("Every electrical part pin must have exactly one explicit pin-to-pad mapping.");
        var mappedPads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in PinMappings)
        {
            if (mapping is null || string.IsNullOrWhiteSpace(mapping.PinNumber) || string.IsNullOrWhiteSpace(mapping.PinName)
                || string.IsNullOrWhiteSpace(mapping.PadNumber) || string.IsNullOrWhiteSpace(mapping.PadName)
                || !electricalPins.Contains((mapping.PinNumber, mapping.PinName)) || !padNumbers.Contains(mapping.PadNumber)
                || !mappedPads.Add(mapping.PadNumber) || unused.Contains(mapping.PadNumber))
                throw Invalid("Pin mappings must match exact part pin number/name pairs and distinct electrical pad identities.");
            var pad = Pads.Single(p => p.Number == mapping.PadNumber);
            if (!pad.Electrical || pad.Name != mapping.PadName) throw Invalid("The mapped pad must be declared electrical with its exact name.");
            Text(mapping.PinNumber); Text(mapping.PinName); Text(mapping.PadNumber); Text(mapping.PadName);
        }
        var unmappedPads = Pads.Where(p => !mappedPads.Contains(p.Number) && !unused.Contains(p.Number)).Select(p => p.Number).ToArray();
        if (unmappedPads.Length != 0) throw Invalid("Every pad must be mapped or explicitly declared unused; no pad is inferred by position.");
        if (unused.Any(p => Pads.Single(x => x.Number == p).Electrical)) throw Invalid("An electrical pad cannot be marked unused.");
    }

    private static void Text(string text)
    {
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw Invalid("Package mapping text must be XML-preservable."); }
    }
    private static AutomationException Invalid(string message) => new("invalid_component_package_mapping", message);
}

public static class ComponentPackageMappingXml
{
    public const string Namespace = "urn:kicad:automation:package-mapping:1";
    private static readonly XNamespace Ns = Namespace;

    public static string Write(ComponentPackageMapping mapping, Circuit circuit)
    {
        mapping.Validate(circuit);
        var root = new XElement(Ns + "package-mapping", new XAttribute("version", 1),
            A("part", mapping.PartId), A("component", mapping.ComponentId),
            new XAttribute("package", mapping.PackageId), new XAttribute("revision", mapping.PackageRevision),
            new XElement(Ns + "pads", mapping.Pads.OrderBy(p => p.Number, StringComparer.Ordinal).Select(p =>
                new XElement(Ns + "pad", new XAttribute("number", p.Number), new XAttribute("name", p.Name), new XAttribute("electrical", p.Electrical)))),
            new XElement(Ns + "pin-mappings", mapping.PinMappings.OrderBy(p => p.PinNumber, StringComparer.Ordinal).Select(p =>
                new XElement(Ns + "mapping", new XAttribute("pin", p.PinNumber), new XAttribute("pin-name", p.PinName),
                    new XAttribute("pad", p.PadNumber), new XAttribute("pad-name", p.PadName)))),
            new XElement(Ns + "unused-pads", mapping.ExplicitlyUnusedPads.OrderBy(p => p, StringComparer.Ordinal).Select(p => new XElement(Ns + "pad", p))));
        return EngineeringXmlText.Render(root);
    }

    public static ComponentPackageMapping Read(string xml, Circuit circuit)
    {
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            if (root.Name != Ns + "package-mapping" || (string?)root.Attribute("version") != "1") throw Invalid("Unsupported package mapping XML.");
            var mapping = new ComponentPackageMapping(
                Id(root, "part"), Id(root, "component"), Text(root, "package"), Text(root, "revision"),
                [.. root.Element(Ns + "pads")!.Elements(Ns + "pad").Select(p => new PackagePad(Text(p, "number"), Text(p, "name"), (bool)p.Attribute("electrical")!))],
                [.. root.Element(Ns + "pin-mappings")!.Elements(Ns + "mapping").Select(p => new PinPadMapping(Text(p, "pin"), Text(p, "pad"), Text(p, "pin-name"), Text(p, "pad-name")))],
                [.. root.Element(Ns + "unused-pads")!.Elements(Ns + "pad").Select(p => p.Value)]);
            mapping.Validate(circuit); return mapping;
        }
        catch (Exception error) when (error is XmlException or FormatException or OverflowException)
        { throw Invalid("Invalid package mapping XML: " + error.Message); }
    }

    private static XAttribute A(string name, Guid value) => new(name, value.ToString("D"));
    private static string Text(XElement e, string name) => e.Attribute(name)?.Value ?? throw Invalid("Missing package mapping attribute " + name + ".");
    private static Guid Id(XElement e, string name) => Guid.ParseExact(Text(e, name), "D");
    private static AutomationException Invalid(string message) => new("invalid_component_package_mapping", message);
}
